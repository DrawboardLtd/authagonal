using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Two themes that share a root: surfaces that report more than the caller has proved they may know,
/// and admin paths that skip the gates every other path enforces.
/// </summary>
public sealed class EnumerationAndAdminPathTests : IAsyncLifetime
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
    // F117 — confirm-email is an anonymous existence oracle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConfirmEmailPage_ForgedToken_TellsTheCallerNothingAboutTheAccount()
    {
        await _factory.SeedTestUserAsync("real@example.com", "Test1234!");

        // The token needs no integrity — InspectConfirmToken only requires base64 splitting into
        // three "||" segments with a parseable expiry — so anyone could ask about any address.
        var realAccount = await FetchConfirmPageAsync(ForgeToken("real@example.com"));
        var noAccount = await FetchConfirmPageAsync(ForgeToken("nobody@example.com"));

        Assert.Equal(realAccount, noAccount);
    }

    [Fact]
    public async Task ConfirmEmailPage_DoesNotEchoTheCallerSuppliedAddress()
    {
        var page = await FetchConfirmPageAsync(ForgeToken("reflected@example.com"));
        Assert.DoesNotContain("reflected@example.com", page);
    }

    [Fact]
    public async Task ConfirmEmailPage_IsNotCacheable()
    {
        var response = await _client.GetAsync(
            $"/api/auth/confirm-email?token={Uri.EscapeDataString(ForgeToken("someone@example.com"))}");

        // The body embeds the single-use token in a form field.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    // -----------------------------------------------------------------------
    // F112 — lockout is an existence oracle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_LockedAccountWithWrongPassword_MatchesAnUnknownAccountExactly()
    {
        await _factory.SeedTestUserAsync("locked@example.com", "Test1234!");
        for (var i = 0; i < 6; i++)
            await LoginAsync("locked@example.com", "Wrong!");

        var locked = await LoginAsync("locked@example.com", "Wrong!");
        var unknown = await LoginAsync("ghost@example.com", "Wrong!");

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(unknown.StatusCode, locked.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await locked.Content.ReadAsStringAsync());
    }

    // -----------------------------------------------------------------------
    // F152 — admin impersonation skips the gates every other mint enforces
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AdminMint_ForDeactivatedUser_IsRefused()
    {
        var user = await _factory.SeedTestUserAsync("deactivated@example.com", "Test1234!");
        user.IsActive = false;
        await _factory.UserStore.UpdateAsync(user);

        // Deactivation revokes grants and rotates the security stamp precisely because "a disabled
        // account that keeps working until its token expires has not been disabled" — but this path
        // called the raw subject builder and issued a usable token anyway.
        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "openid");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminMint_ForDisabledClient_IsRefused()
    {
        var user = await _factory.SeedTestUserAsync("someone@example.com", "Test1234!");

        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.Enabled = false;
        await _factory.ClientStore.UpsertAsync(client);

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "openid");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminMint_DropsRoleGatedScopesTheTargetDoesNotQualifyFor()
    {
        var user = await _factory.SeedTestUserAsync("plain@example.com", "Test1234!");

        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "privileged.read",
            AllowedRoles = ["operator"],
        });
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedScopes.Add("privileged.read");
        await _factory.ClientStore.UpsertAsync(client);

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "openid privileged.read");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The scope gate is documented as applying on every path that mints a token for a human.
        // This is the one path where the caller chooses both the user and the scopes.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("privileged.read", json.GetProperty("scope").GetString());
    }

    // -----------------------------------------------------------------------
    // F137 — admin MFA reset
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AdminMfaReset_RotatesTheSecurityStamp_SoLiveSessionsDie()
    {
        var user = await _factory.SeedTestUserAsync("mfa@example.com", "Test1234!");
        var stampBefore = user.SecurityStamp;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/profile/{user.Id}/mfa"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Cookie validation revalidates against the stamp, so without rotating it every existing
        // mfa_authenticated session survived the reset — including the attacker's, which is the
        // session an admin resetting MFA is most likely trying to cut off.
        var after = await _factory.UserStore.GetAsync(user.Id);
        Assert.NotEqual(stampBefore, after!.SecurityStamp);
        Assert.False(after.MfaEnabled);
    }

    // -----------------------------------------------------------------------
    // F121 — self-service attributes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Registration_CannotSetReservedProtocolClaimsViaCustomAttributes()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "attrs@example.com",
            password = "NewPass1234!",
            customAttributes = new Dictionary<string, string>
            {
                ["org_id"] = "org-i-do-not-belong-to",
                ["federated_connection"] = "trusted-idp",
                ["harmless"] = "fine",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var user = await _factory.UserStore.FindByEmailAsync("attrs@example.com");
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);

        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "leaky",
            UserClaims = ["org_id", "federated_connection", "harmless"],
        });
        client!.AllowedScopes.Add("leaky");
        await _factory.ClientStore.UpsertAsync(client);

        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<Authagonal.Protocol.Services.IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<Authagonal.Server.Services.UserStoreOidcSubjectResolver>();
        var subject = await resolver.BuildSubjectAsync(user!, client);

        var claims = ReadClaims(await tokens.CreateAccessTokenAsync(subject, client, ["openid", "leaky"]));

        // org_id is a first-class claim and federated_connection is the marker JIT provisioning writes
        // to record that an account came from a trusted upstream. Neither may be asserted from
        // user-controlled storage.
        Assert.False(claims.ContainsKey("org_id"));
        Assert.False(claims.ContainsKey("federated_connection"));
        Assert.Equal("fine", claims["harmless"]);
    }

    [Fact]
    public async Task Registration_BoundsTheNumberOfCustomAttributes()
    {
        var many = new Dictionary<string, string>();
        for (var i = 0; i < 200; i++) many[$"key{i}"] = "v";

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "flood@example.com",
            password = "NewPass1234!",
            customAttributes = many,
        });

        var user = await _factory.UserStore.FindByEmailAsync("flood@example.com");
        Assert.True(user!.CustomAttributes.Count <= 32, $"stored {user.CustomAttributes.Count} attributes");
    }

    [Fact]
    public async Task Registration_DropsOverlongAttributeValues()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "long@example.com",
            password = "NewPass1234!",
            customAttributes = new Dictionary<string, string> { ["big"] = new('x', 5000) },
        });

        var user = await _factory.UserStore.FindByEmailAsync("long@example.com");
        Assert.False(user!.CustomAttributes.ContainsKey("big"));
    }

    // -----------------------------------------------------------------------
    // F61 — agent ceiling disclosure
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AgentConsentInfo_IsNotAnonymous()
    {
        var anonymous = await _client.GetAsync("/consent/agents/some-agent/info");

        // Its three siblings all require authorization; this one disclosed the agent's complete
        // RFC 9396 ceiling and every per-action policy to any anonymous caller. Asserted as "does not
        // serve the body" rather than a specific status, because the challenge shape is the host's
        // (a cookie scheme redirects) and matching the siblings is the point.
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.DoesNotContain("ceiling", await anonymous.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // And the sibling it should now match behaves identically.
        var sibling = await _client.GetAsync("/consent/agents");
        Assert.Equal(sibling.StatusCode, anonymous.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ForgeToken(string email)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"not-a-real-stamp||{email}||{expiry}"));
    }

    private async Task<string> FetchConfirmPageAsync(string token)
    {
        var response = await _client.GetAsync($"/api/auth/confirm-email?token={Uri.EscapeDataString(token)}");
        var body = await response.Content.ReadAsStringAsync();
        // The token rides the form field, so strip it before comparing two pages.
        return body.Replace(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(token), "TOKEN");
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<HttpResponseMessage> MintAsync(string clientId, string userId, string scopes)
    {
        var url = $"/api/v1/token?clientId={clientId}&userId={userId}&scopes={Uri.EscapeDataString(scopes)}";
        return await _client.SendAsync(AdminRequest(HttpMethod.Post, url));
    }

    private string? _adminToken;

    private HttpRequestMessage AdminRequest(HttpMethod method, string url)
    {
        _adminToken ??= _factory.GetAdminTokenAsync(_client).GetAwaiter().GetResult();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return request;
    }

    private static Dictionary<string, string> ReadClaims(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/')
            .PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal);
    }
}
