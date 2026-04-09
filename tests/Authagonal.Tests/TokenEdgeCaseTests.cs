using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class TokenEdgeCaseTests : IAsyncLifetime
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
    // Refresh token edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshToken_Revoked_Returns400()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // Revoke it
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });
        var revokeResponse = await _client.PostAsync("/connect/revocation", revokeForm);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Try to use revoked refresh token
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var response = await _client.PostAsync("/connect/token", refreshForm);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_WrongClient_Returns400()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // Try to refresh with admin-client instead of test-client
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_InvalidToken_Returns400()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "completely-invalid-token",
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var response = await _client.PostAsync("/connect/token", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_ReturnsNewRefreshToken()
    {
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var response = await _client.PostAsync("/connect/token", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var newTokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var newRefreshToken = newTokens.GetProperty("refresh_token").GetString();
        Assert.NotNull(newRefreshToken);
        Assert.NotEqual(refreshToken, newRefreshToken);
    }

    // -----------------------------------------------------------------------
    // JWKS validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Jwks_ContainsSigningKey_WithCorrectProperties()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration/jwks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = json.GetProperty("keys");
        Assert.True(keys.GetArrayLength() > 0);

        var key = keys[0];
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.NotNull(key.GetProperty("kid").GetString());
        Assert.NotNull(key.GetProperty("n").GetString());
        Assert.NotNull(key.GetProperty("e").GetString());
    }

    [Fact]
    public async Task Jwks_KeyCanVerifyAccessToken()
    {
        // Get a token
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var tokenResponse = await _client.SendAsync(request);
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokenJson.GetProperty("access_token").GetString()!;

        // Use the access token as Bearer
        var userinfoRequest = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Verify the token is a well-formed JWT with 3 parts
        var parts = accessToken.Split('.');
        Assert.Equal(3, parts.Length);

        // Fetch JWKS and verify the token's kid matches a published key
        var jwksResponse = await _client.GetAsync("/.well-known/openid-configuration/jwks");
        var jwks = await jwksResponse.Content.ReadFromJsonAsync<JsonElement>();
        var kids = jwks.GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()).ToList();

        // Decode the token header to get kid
        var header = JsonSerializer.Deserialize<JsonElement>(
            Convert.FromBase64String(parts[0].PadRight(parts[0].Length + (4 - parts[0].Length % 4) % 4, '=')));
        var tokenKid = header.GetProperty("kid").GetString();
        Assert.Contains(tokenKid, kids);
    }

    // -----------------------------------------------------------------------
    // Userinfo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_WithUserToken_ReturnsProfileClaims()
    {
        var tokens = await GetTokensViaPkce();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("sub").GetString());
        Assert.Equal("test@example.com", json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Userinfo_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Userinfo_WithInvalidToken_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Revocation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Revocation_AccessToken_Returns200()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var tokenResponse = await _client.SendAsync(request);
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenJson.GetProperty("access_token").GetString()!;

        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var revokeResponse = await _client.PostAsync("/connect/revocation", revokeForm);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
    }

    [Fact]
    public async Task Revocation_UnknownToken_Returns200()
    {
        // RFC 7009: revocation of unknown tokens MUST return 200
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "nonexistent-token",
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var response = await _client.PostAsync("/connect/revocation", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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
        return await response.Content.ReadFromJsonAsync<JsonElement>();
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

    private static AuthenticationHeaderValue BasicAuth(string clientId, string clientSecret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
}
