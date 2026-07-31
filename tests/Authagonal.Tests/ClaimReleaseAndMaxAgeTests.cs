using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// What a scope actually buys, and whether a re-authentication demand is honoured.
/// </summary>
/// <remarks>
/// OIDC Core §5.4 binds each standard claim set to a scope. The ID token released the email and
/// profile sets on <c>openid</c> — mandatory on every OIDC request — so there was no request that did
/// NOT disclose them, and userinfo appended org_id, roles and full SCIM group membership to every
/// response with no gate at all. §3.1.2.1's <c>max_age</c> was read nowhere in the product.
/// </remarks>
public sealed class ClaimReleaseAndMaxAgeTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F189 — ID token claim gating
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IdToken_WithOpenidAlone_DoesNotLeakEmailOrProfile()
    {
        var tokens = await GetTokensAsync("openid");
        var idToken = PayloadOf(tokens.GetProperty("id_token").GetString()!);

        Assert.False(idToken.TryGetProperty("email", out _));
        Assert.False(idToken.TryGetProperty("given_name", out _));
        Assert.False(idToken.TryGetProperty("family_name", out _));
        Assert.False(idToken.TryGetProperty("name", out _));
        Assert.False(idToken.TryGetProperty("phone_number", out _));

        // sub is always released — it is what the token is about.
        Assert.Equal(_seededUserId, idToken.GetProperty("sub").GetString());
    }

    [Fact]
    public async Task IdToken_WithEmailScope_ReleasesEmailOnly()
    {
        var idToken = PayloadOf((await GetTokensAsync("openid email")).GetProperty("id_token").GetString()!);

        Assert.Equal("test@example.com", idToken.GetProperty("email").GetString());
        Assert.False(idToken.TryGetProperty("given_name", out _));
    }

    [Fact]
    public async Task IdToken_PhoneNumber_RequiresPhoneScopeNotProfile()
    {
        // §5.4 assigns phone_number to `phone`. It rode `profile`, so the consent screen never told
        // the user their phone number was being disclosed.
        var withProfile = PayloadOf((await GetTokensAsync("openid profile")).GetProperty("id_token").GetString()!);
        Assert.False(withProfile.TryGetProperty("phone_number", out _));

        var withPhone = PayloadOf((await GetTokensAsync("openid phone")).GetProperty("id_token").GetString()!);
        Assert.Equal("+15550100", withPhone.GetProperty("phone_number").GetString());
    }

    [Fact]
    public async Task IdToken_RolesAndGroups_RequireTheirOwnScopes()
    {
        var withProfile = PayloadOf((await GetTokensAsync("openid profile")).GetProperty("id_token").GetString()!);
        Assert.False(withProfile.TryGetProperty("roles", out _));

        var withRoles = PayloadOf((await GetTokensAsync("openid roles")).GetProperty("id_token").GetString()!);
        Assert.Contains("tester", withRoles.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task IdToken_AlwaysIncludeUserClaims_RestoresUngatedRelease()
    {
        // The documented opt-out, which was persisted, seeded and migrated but read nowhere — so
        // operators had a knob implying this was already gated. Honouring it is also the migration
        // path for clients that relied on the old behaviour.
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AlwaysIncludeUserClaimsInIdToken = true;
        await _factory.ClientStore.UpsertAsync(client);

        var idToken = PayloadOf((await GetTokensAsync("openid")).GetProperty("id_token").GetString()!);
        Assert.Equal("test@example.com", idToken.GetProperty("email").GetString());
    }

    // -----------------------------------------------------------------------
    // F187 — userinfo claim gating
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_WithoutRolesOrGroupsScope_ReleasesNeither()
    {
        var tokens = await GetTokensAsync("openid profile email");
        var body = await CallUserinfoAsync(tokens.GetProperty("access_token").GetString()!);

        Assert.False(body.TryGetProperty("roles", out _));
        Assert.False(body.TryGetProperty("groups", out _));
        Assert.Equal("test@example.com", body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Userinfo_WithRolesScope_ReleasesRoles()
    {
        var tokens = await GetTokensAsync("openid roles");
        var body = await CallUserinfoAsync(tokens.GetProperty("access_token").GetString()!);

        Assert.Contains("tester", body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Userinfo_PhoneNumber_RequiresPhoneScope()
    {
        var withProfile = await CallUserinfoAsync(
            (await GetTokensAsync("openid profile")).GetProperty("access_token").GetString()!);
        Assert.False(withProfile.TryGetProperty("phone_number", out _));

        var withPhone = await CallUserinfoAsync(
            (await GetTokensAsync("openid phone")).GetProperty("access_token").GetString()!);
        Assert.Equal("+15550100", withPhone.GetProperty("phone_number").GetString());
    }

    // -----------------------------------------------------------------------
    // F58 / F196 — max_age
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MaxAge_ZeroAgainstAnExistingSession_ForcesReauthentication()
    {
        await LoginAsync();

        // The session is valid and would otherwise issue a code straight away.
        var withoutMaxAge = await _client.GetAsync(AuthorizeUrl("openid"));
        Assert.DoesNotContain("/login", withoutMaxAge.Headers.Location!.ToString());

        var withMaxAge = await _client.GetAsync(AuthorizeUrl("openid") + "&max_age=0");
        Assert.Equal(HttpStatusCode.Redirect, withMaxAge.StatusCode);
        Assert.Contains("/login", withMaxAge.Headers.Location!.ToString());
    }

    [Fact]
    public async Task MaxAge_LargeEnoughToCoverTheSession_IsSatisfied()
    {
        await LoginAsync();

        var response = await _client.GetAsync(AuthorizeUrl("openid") + "&max_age=3600");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task MaxAge_ForcedReauthDoesNotLoop_TheDemandIsStrippedForTheRoundTrip()
    {
        await LoginAsync();

        var forced = await _client.GetAsync(AuthorizeUrl("openid") + "&max_age=0");
        var loginRedirect = forced.Headers.Location!.ToString();

        // max_age=0 can never be satisfied by a session that has existed for a measurable moment, so
        // carrying it onto the return URL would bounce the user back to login forever.
        Assert.DoesNotContain("max_age", loginRedirect);
    }

    [Fact]
    public async Task MaxAge_Malformed_IsRefusedRatherThanIgnored()
    {
        await LoginAsync();

        var response = await _client.GetAsync(AuthorizeUrl("openid") + "&max_age=not-a-number");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // A demand the OP cannot parse must not degrade into "no demand" — that is indistinguishable
        // from having honoured it.
        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=invalid_request", location);
    }

    [Fact]
    public async Task IdToken_CarriesAuthTime()
    {
        // Advertised in claims_supported since 0.11.0 but never emitted, so max_age had nothing to
        // compare against and an RP could not verify its demand had been met.
        await LoginAsync();
        var tokens = await CompleteCodeFlowAsync("openid");
        var idToken = PayloadOf(tokens.GetProperty("id_token").GetString()!);

        var authTime = idToken.GetProperty("auth_time").GetInt64();
        Assert.InRange(authTime, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Discovery_AdvertisesTheScopesThatGovernTheClaimsItLists()
    {
        var doc = await (await _client.GetAsync("/.well-known/openid-configuration"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var scopes = doc.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()).ToArray();

        // claims_supported named phone_number while no advertised scope governed it.
        Assert.Contains("phone", scopes);
        Assert.Contains("roles", scopes);
        Assert.Contains("groups", scopes);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string _seededUserId = "";

    private static JsonElement PayloadOf(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/')
            .PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }

    private async Task<JsonElement> CallUserinfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task LoginAsync()
    {
        var user = await _factory.SeedTestUserAsync();
        user.Phone = "+15550100";
        user.Roles.Add("tester");
        await _factory.UserStore.UpdateAsync(user);
        _seededUserId = user.Id;

        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        foreach (var scope in new[] { "phone", "roles", "groups" })
            if (!client!.AllowedScopes.Contains(scope))
                client.AllowedScopes.Add(scope);
        await _factory.ClientStore.UpsertAsync(client!);

        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private string _verifier = "";

    private string AuthorizeUrl(string scope)
    {
        var (verifier, challenge) = GeneratePkce();
        _verifier = verifier;
        return $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope={Uri.EscapeDataString(scope)}" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";
    }

    private async Task<JsonElement> GetTokensAsync(string scope)
    {
        await LoginAsync();
        return await CompleteCodeFlowAsync(scope);
    }

    private async Task<JsonElement> CompleteCodeFlowAsync(string scope)
    {
        var authResponse = await _client.GetAsync(AuthorizeUrl(scope));
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = _verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
