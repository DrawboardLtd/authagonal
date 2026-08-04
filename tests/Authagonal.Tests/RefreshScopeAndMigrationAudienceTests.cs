using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A refresh chain that outlived the permission it was granted under.
/// </summary>
/// <remarks>
/// The refresh path re-applies the per-USER role gate, with a comment saying "this is where revoking a role
/// actually takes effect: the grant still records the scopes approved at authorize, so without this a refresh
/// chain would keep re-minting a gated scope for as long as the refresh token lived". The client-level
/// equivalent was missing, so the same sentence was true of <c>AllowedScopes</c> and nothing acted on it.
/// <para>
/// Responding to an incident by PUTting <c>/api/v1/clients/{id}</c> without a scope therefore refused every NEW
/// authorization request naming it while every existing refresh chain kept re-minting it, for up to
/// <c>AbsoluteRefreshTokenLifetimeSeconds</c> — 30 days on the defaults. The operator is told the permission is
/// gone and the tokens say otherwise.
/// </para>
/// </remarks>
public sealed class RefreshScopeAndMigrationAudienceTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedScopes = ["openid", "profile", "email", "offline_access", "reports.read"];
        client.AllowOfflineAccess = true;
        await _factory.ClientStore.UpsertAsync(client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task RemovingAScopeFromTheClient_StopsTheRefreshChainMintingIt()
    {
        var tokens = await GetTokensAsync("openid profile email offline_access reports.read");
        Assert.Contains("reports.read", tokens.GetProperty("scope").GetString()!, StringComparison.Ordinal);

        // The documented way to stop an application requesting a permission.
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedScopes = ["openid", "profile", "email", "offline_access"];
        await _factory.ClientStore.UpsertAsync(client);

        var refreshed = await RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        refreshed.EnsureSuccessStatusCode();

        var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var scope = body.GetProperty("scope").GetString()!;

        Assert.DoesNotContain("reports.read", scope, StringComparison.Ordinal);
        // The rest survives — a client that lost one scope keeps working, as the role gate beside it does.
        Assert.Contains("openid", scope, StringComparison.Ordinal);
        Assert.Contains("profile", scope, StringComparison.Ordinal);
    }

    /// <summary>Losing every scope ends the chain rather than issuing a token the client cannot use.</summary>
    [Fact]
    public async Task LosingEveryScope_EndsTheRefreshChain()
    {
        var tokens = await GetTokensAsync("openid profile offline_access");

        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedScopes = ["something.else"];
        await _factory.ClientStore.UpsertAsync(client);

        var refreshed = await RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, refreshed.StatusCode);
    }

    /// <summary>The control: an untouched client refreshes with its scopes intact.</summary>
    [Fact]
    public async Task AnUntouchedClientKeepsItsScopesOnRefresh()
    {
        var tokens = await GetTokensAsync("openid profile email offline_access reports.read");

        var refreshed = await RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        refreshed.EnsureSuccessStatusCode();

        var scope = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scope").GetString()!;
        Assert.Contains("reports.read", scope, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<JsonElement> GetTokensAsync(string scope)
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorize = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope={Uri.EscapeDataString(scope)}" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256");
        var code = HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
