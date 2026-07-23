using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// RFC 8693 token exchange: a primary access token is presented at /connect/token and a
/// short-lived, downscoped access token comes back. Exchange never escalates scope, never
/// outlives the subject token, never issues a refresh token, and re-gates custom claims
/// by the NEW scope set.
/// </summary>
public sealed class TokenExchangeTests : IAsyncLifetime
{
    private const string AppScope = "projects-api.read";
    private const string OtherScope = "projects-admin";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        // test-client: allow the exchange grant and two app scopes; only AppScope releases
        // the custom claim, and only AppScope is ever granted on the primary token.
        var client = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedGrantTypes = [.. client.AllowedGrantTypes, GrantTypes.TokenExchange];
        client.AllowedScopes = [.. client.AllowedScopes, AppScope, OtherScope];
        await _factory.ClientStore.UpsertAsync(client);

        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = AppScope,
            UserClaims = ["org_role"],
        });
        await _factory.ScopeStore.CreateAsync(new Scope { Name = OtherScope });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Exchange_DownscopesAndPreservesSubjectAndGatedClaims()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        var response = await ExchangeAsync(primary, scope: $"openid {AppScope}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", body.GetProperty("issued_token_type").GetString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.False(body.TryGetProperty("refresh_token", out _));
        Assert.Equal($"openid {AppScope}", body.GetProperty("scope").GetString());

        var primaryClaims = ReadClaims(primary);
        var exchanged = ReadClaims(body.GetProperty("access_token").GetString()!);
        Assert.Equal(primaryClaims["sub"], exchanged["sub"]);
        Assert.Equal("admin", exchanged["org_role"]); // released: AppScope is in the new scope set
        Assert.Equal($"openid {AppScope}", exchanged["scope"]);
    }

    [Fact]
    public async Task Exchange_RegatesCustomClaims_ByNewScopeSet()
    {
        var primary = await GetPrimaryAccessTokenAsync();
        Assert.Equal("admin", ReadClaims(primary)["org_role"]); // present on the primary

        var response = await ExchangeAsync(primary, scope: "openid profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var exchanged = ReadClaims(body.GetProperty("access_token").GetString()!);
        Assert.False(exchanged.ContainsKey("org_role")); // dropped: no scope in the new set releases it
    }

    [Fact]
    public async Task Exchange_NeverOutlivesSubjectToken()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        var response = await ExchangeAsync(primary, scope: "openid");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var primaryExp = long.Parse(ReadClaims(primary)["exp"]);
        var exchangedExp = long.Parse(ReadClaims(body.GetProperty("access_token").GetString()!)["exp"]);
        Assert.True(exchangedExp <= primaryExp,
            $"exchanged token exp {exchangedExp} must not exceed subject token exp {primaryExp}");
    }

    [Fact]
    public async Task Exchange_ScopeEscalation_IsRejected()
    {
        // OtherScope is allowed for the client but was never granted on the primary token.
        var primary = await GetPrimaryAccessTokenAsync();

        var response = await ExchangeAsync(primary, scope: OtherScope);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_WithoutScopeParam_InheritsIntersection_MinusOfflineAccess()
    {
        var primary = await GetPrimaryAccessTokenAsync(); // granted: openid profile email offline_access AppScope

        var response = await ExchangeAsync(primary, scope: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var granted = body.GetProperty("scope").GetString()!.Split(' ');
        Assert.Contains("openid", granted);
        Assert.Contains(AppScope, granted);
        Assert.DoesNotContain("offline_access", granted);
        Assert.False(body.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task Exchange_GarbageSubjectToken_IsRejected()
    {
        var response = await ExchangeAsync("not-a-jwt", scope: "openid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_ActorToken_IsRejected()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        var form = BaseExchangeForm(primary);
        form["actor_token"] = primary;
        form["actor_token_type"] = "urn:ietf:params:oauth:token-type:access_token";

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_UnknownSubjectTokenType_IsRejected()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        var form = BaseExchangeForm(primary);
        form["subject_token_type"] = "urn:ietf:params:oauth:token-type:saml2";

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exchange_ClientWithoutGrant_IsRejected()
    {
        var primary = await GetPrimaryAccessTokenAsync();

        // Strip the grant again: the endpoint's client-grant check must refuse the exchange.
        var client = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedGrantTypes = client.AllowedGrantTypes.Where(g => g != GrantTypes.TokenExchange).ToList();
        await _factory.ClientStore.UpsertAsync(client);

        var response = await ExchangeAsync(primary, scope: "openid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized_client", body.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------

    private async Task<string> GetPrimaryAccessTokenAsync()
    {
        var user = await _factory.SeedTestUserAsync();
        user.CustomAttributes["org_role"] = "admin";
        await _factory.UserStore.UpdateAsync(user);

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope={Uri.EscapeDataString($"openid profile email offline_access {AppScope}")}" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var response = await _client.PostAsync("/connect/token", tokenForm);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> ExchangeAsync(string subjectToken, string? scope)
    {
        var form = BaseExchangeForm(subjectToken);
        if (scope is not null)
            form["scope"] = scope;
        return _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private static Dictionary<string, string> BaseExchangeForm(string subjectToken) => new()
    {
        ["grant_type"] = GrantTypes.TokenExchange,
        ["subject_token"] = subjectToken,
        ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
        ["client_id"] = AuthagonalTestFactory.TestClientId,
    };

    private static Dictionary<string, string> ReadClaims(string jwt)
    {
        var token = new JsonWebTokenHandler().ReadJsonWebToken(jwt);
        var dict = new Dictionary<string, string>();
        foreach (var claim in token.Claims)
            dict[claim.Type] = claim.Value;
        return dict;
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
