using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class OAuthEndpointTests : IAsyncLifetime
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
    // POST /connect/token — client_credentials
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ClientCredentials_ValidSecret_ReturnsAccessToken()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.True(json.GetProperty("expires_in").GetInt32() > 0);
    }

    [Fact]
    public async Task ClientCredentials_InvalidSecret_Returns401()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, "wrong-secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClientCredentials_UnknownClient_Returns401()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth("nonexistent", "secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClientCredentials_DisallowedGrantType_Returns400()
    {
        // test-client only allows authorization_code and refresh_token
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var response = await _client.PostAsync("/connect/token", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized_client", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClientCredentials_MissingGrantType_Returns400()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClientCredentials_UnsupportedGrantType_Returns400()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "implicit"
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClientCredentials_ClientSecretPost_Works()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var response = await _client.PostAsync("/connect/token", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task ClientCredentials_FiresAuthHook()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        await _client.SendAsync(request);

        Assert.Contains(_factory.AuthHook.TokenIssuances,
            t => t.ClientId == AuthagonalTestFactory.AdminClientId && t.GrantType == "client_credentials");
    }

    // -----------------------------------------------------------------------
    // GET /connect/authorize
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Authorize_NotAuthenticated_RedirectsToLogin()
    {
        var url = BuildAuthorizeUrl(AuthagonalTestFactory.TestClientId, "https://app.test/callback");

        var response = await _client.GetAsync(url);

        // Should redirect to login page
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Authorize_InvalidClientId_RedirectsWithError()
    {
        var url = BuildAuthorizeUrl("nonexistent-client", "https://app.test/callback");

        var response = await _client.GetAsync(url);

        // OAuth spec: when redirect_uri is provided, errors are redirected back to the client
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=unauthorized_client", location);
    }

    [Fact]
    public async Task Authorize_InvalidRedirectUri_Returns400()
    {
        var url = BuildAuthorizeUrl(AuthagonalTestFactory.TestClientId, "https://evil.com/callback");

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_Authenticated_RedirectsWithCode()
    {
        // Login first
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (codeVerifier, codeChallenge) = GeneratePkce();
        var url = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            codeChallenge: codeChallenge);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://app.test/callback", location);
        Assert.Contains("code=", location);
    }

    [Fact]
    public async Task Authorize_MissingPkce_WhenRequired_RedirectsWithError()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // test-client requires PKCE but we don't send code_challenge
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
                  $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
                  "&response_type=code&scope=openid&state=test123";

        var response = await _client.GetAsync(url);

        // OAuth spec: error is redirected back to redirect_uri with error params
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=invalid_request", location);
        Assert.Contains("code_challenge", location);
    }

    // -----------------------------------------------------------------------
    // Full authorization code + PKCE flow
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AuthorizationCodeFlow_FullPkceExchange_ReturnsTokens()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (codeVerifier, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            scope: "openid profile email offline_access",
            codeChallenge: codeChallenge);

        // Step 1: Authorize → get code
        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

        var callbackUri = authorizeResponse.Headers.Location!;
        var queryParams = HttpUtility.ParseQueryString(callbackUri.Query);
        var code = queryParams["code"]!;
        Assert.NotNull(code);

        // Step 2: Exchange code for tokens
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = codeVerifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(tokens.GetProperty("access_token").GetString());
        Assert.NotNull(tokens.GetProperty("id_token").GetString());
        Assert.NotNull(tokens.GetProperty("refresh_token").GetString());
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);
    }

    [Fact]
    public async Task AuthorizationCodeFlow_WrongCodeVerifier_Fails()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (_, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            codeChallenge: codeChallenge);

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        var callbackUri = authorizeResponse.Headers.Location!;
        var code = HttpUtility.ParseQueryString(callbackUri.Query)["code"]!;

        // Use a different verifier
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = "totally-wrong-verifier-that-doesnt-match-the-challenge",
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task AuthorizationCodeFlow_CodeReuse_Fails()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (codeVerifier, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            codeChallenge: codeChallenge);

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = codeVerifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        // First exchange succeeds
        var first = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second exchange with same code fails
        var second = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Refresh token flow
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshToken_ValidToken_ReturnsNewAccessToken()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Get initial tokens via auth code flow
        var (codeVerifier, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            scope: "openid offline_access",
            codeChallenge: codeChallenge);

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = codeVerifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // Refresh
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var refreshResponse = await _client.PostAsync("/connect/token", refreshForm);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var newTokens = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(newTokens.GetProperty("access_token").GetString());
    }

    // -----------------------------------------------------------------------
    // POST /connect/revocation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Revocation_ValidToken_Returns200()
    {
        // Get a token first
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);
        var tokenResponse = await _client.SendAsync(request);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // Revoke
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = accessToken,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var revokeResponse = await _client.PostAsync("/connect/revocation", revokeForm);

        // RFC 7009: always 200
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
    }

    // -----------------------------------------------------------------------
    // GET /connect/userinfo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_ValidBearerToken_ReturnsUserClaims()
    {
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (codeVerifier, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(
            AuthagonalTestFactory.TestClientId,
            "https://app.test/callback",
            scope: "openid profile email",
            codeChallenge: codeChallenge);

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = codeVerifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });

        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // Call userinfo
        var userinfoRequest = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var userinfoResponse = await _client.SendAsync(userinfoRequest);

        Assert.Equal(HttpStatusCode.OK, userinfoResponse.StatusCode);
        var userinfo = await userinfoResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, userinfo.GetProperty("sub").GetString());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AuthenticationHeaderValue BasicAuth(string clientId, string clientSecret)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    private static string BuildAuthorizeUrl(
        string clientId,
        string redirectUri,
        string scope = "openid",
        string? codeChallenge = null)
    {
        codeChallenge ??= GeneratePkce().Challenge;

        return $"/connect/authorize?client_id={clientId}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&scope={Uri.EscapeDataString(scope)}" +
               $"&state=test-state-123" +
               $"&code_challenge={codeChallenge}" +
               $"&code_challenge_method=S256";
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (verifier, challenge);
    }
}
