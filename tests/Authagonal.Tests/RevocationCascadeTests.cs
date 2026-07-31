using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// What revocation must actually reach. Access tokens here are self-contained ES256 JWTs — there is
/// no reference-token mode — so removing a grant row does nothing to the tokens already minted under
/// it. Every path that revokes a grant has to write those tokens' jtis to
/// <c>IRevokedTokenStore</c>, and each of these tests pins one that did not.
/// </summary>
public sealed class RevocationCascadeTests : IAsyncLifetime
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
    // F194 — RFC 7009 §2.1: revoking a refresh token invalidates the access
    // tokens issued under the same grant.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RevokingRefreshToken_AlsoRevokesTheAccessTokenMintedWithIt()
    {
        var tokens = await GetTokensViaPkce();
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // The access token works before revocation, so a later 401 means revocation and not
        // some unrelated rejection.
        Assert.Equal(HttpStatusCode.OK, (await CallUserinfoAsync(accessToken)).StatusCode);

        await RevokeAsync(refreshToken);

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(accessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallUserinfoAsync(accessToken)).StatusCode);
    }

    [Fact]
    public async Task RevokingRotatedRefreshToken_RevokesAccessTokensFromEarlierRotations()
    {
        // The family's earlier access tokens are still inside their own lifetime, so revoking the
        // current refresh token has to reach them too — otherwise the surviving window is however
        // long ago the client last rotated, not zero.
        var tokens = await GetTokensViaPkce();
        var firstAccessToken = tokens.GetProperty("access_token").GetString()!;

        var rotated = await RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        var secondAccessToken = rotated.GetProperty("access_token").GetString()!;
        var currentRefreshToken = rotated.GetProperty("refresh_token").GetString()!;

        Assert.NotEqual(JtiOf(firstAccessToken), JtiOf(secondAccessToken));

        await RevokeAsync(currentRefreshToken);

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(secondAccessToken)));
        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(firstAccessToken)));
    }

    [Fact]
    public async Task RefreshTokenReplay_RevokesTheAccessTokenTheThiefAlreadyHolds()
    {
        // The server's own theft-detection path. It revoked the refresh family and left the access
        // token minted from the stolen refresh token working — the one credential nobody but the
        // thief has ever seen, so no operator could revoke it by any other means.
        var tokens = await GetTokensViaPkce();
        var stolenRefreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var thiefTokens = await RefreshAsync(stolenRefreshToken);
        var thiefAccessToken = thiefTokens.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await CallUserinfoAsync(thiefAccessToken)).StatusCode);

        // The legitimate client now presents the same refresh token: replay detected.
        var replay = await PostRefreshAsync(stolenRefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(thiefAccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallUserinfoAsync(thiefAccessToken)).StatusCode);
    }

    // -----------------------------------------------------------------------
    // F243 — replay revocation is token authority, not a purge of the user's
    // recorded decisions.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshTokenReplay_LeavesStandingAgentConsentIntact()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var subjectId = (await _factory.UserStore.FindByEmailAsync("test@example.com"))!.Id;

        var consentKey = $"agent_consent:{subjectId}:{AuthagonalTestFactory.TestClientId}";
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = consentKey,
            Type = PersistedGrantTypes.AgentConsent,
            SubjectId = subjectId,
            ClientId = AuthagonalTestFactory.TestClientId,
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });

        await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefreshAsync(refreshToken)).StatusCode);

        // A stolen refresh token compromises token authority. It is not the user withdrawing a
        // standing decision, so the record of that decision must survive.
        Assert.NotNull(await _factory.GrantStore.GetAsync(consentKey));
    }

    // -----------------------------------------------------------------------
    // F199 — "Revoke" on the Authorized Apps page.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RevokingConsent_KillsTheAppsRefreshToken()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var response = await _client.DeleteAsync($"/consent/grants/{AuthagonalTestFactory.TestClientId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Before this, the button removed the consent row only: the app kept rotating this token
        // and minting access tokens for up to AbsoluteRefreshTokenLifetimeSeconds.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefreshAsync(refreshToken)).StatusCode);
    }

    [Fact]
    public async Task RevokingConsent_KillsTheAppsLiveAccessToken()
    {
        var tokens = await GetTokensViaPkce();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await CallUserinfoAsync(accessToken)).StatusCode);

        await _client.DeleteAsync($"/consent/grants/{AuthagonalTestFactory.TestClientId}");

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(accessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallUserinfoAsync(accessToken)).StatusCode);
    }

    [Fact]
    public async Task RevokingConsent_LeavesOtherClientsGrantsAlone()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var subjectId = (await _factory.UserStore.FindByEmailAsync("test@example.com"))!.Id;

        var otherClientKey = $"consent:{subjectId}:some-other-client";
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = otherClientKey,
            Type = PersistedGrantTypes.Consent,
            SubjectId = subjectId,
            ClientId = "some-other-client",
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });

        await _client.DeleteAsync($"/consent/grants/{AuthagonalTestFactory.TestClientId}");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefreshAsync(refreshToken)).StatusCode);
        Assert.NotNull(await _factory.GrantStore.GetAsync(otherClientKey));
    }

    // -----------------------------------------------------------------------
    // F201 — a replayed authorization code revokes what the first redemption issued
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplayedAuthorizationCode_RevokesTheTokensItAlreadyIssued()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authResponse = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            "&response_type=code&scope=openid+offline_access&state=t" +
            $"&code_challenge={challenge}&code_challenge_method=S256");
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        };

        var first = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        first.EnsureSuccessStatusCode();
        var tokens = await first.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await CallUserinfoAsync(accessToken)).StatusCode);

        // RFC 6749 §4.1.2: deny AND revoke. The denial was there; the revocation was not, so a
        // server with positive evidence that a code had been intercepted left the legitimate
        // redemption's tokens live and did nothing with the evidence.
        var replay = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        Assert.True(await _factory.RevokedTokenStore.IsRevokedAsync(JtiOf(accessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallUserinfoAsync(accessToken)).StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>The <c>jti</c> claim, which is the only handle a self-contained access token can be
    /// revoked by.</summary>
    private static string JtiOf(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/')
            .PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
        return doc.RootElement.GetProperty("jti").GetString()!;
    }

    private Task<HttpResponseMessage> CallUserinfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _client.SendAsync(request);
    }

    private async Task RevokeAsync(string token)
    {
        var response = await _client.PostAsync("/connect/revocation", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = token,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostRefreshAsync(string refreshToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<JsonElement> RefreshAsync(string refreshToken)
    {
        var response = await PostRefreshAsync(refreshToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetTokensViaPkce()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile+email+offline_access" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

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

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
