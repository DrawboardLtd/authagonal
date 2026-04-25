using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Covers the GET /connect/authorize endpoint with focus on RFC 8707 resource indicators.
/// Broader OAuth happy-path coverage lives in OAuthEndpointTests.
/// </summary>
public sealed class AuthorizeEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // RFC 8707 — Resource indicator validation at /connect/authorize
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Authorize_ResourceNotAbsoluteUri_RedirectsWithInvalidTarget()
    {
        var url = BuildAuthorizeUrl(resources: ["relative-resource"]);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("invalid_target", query["error"]);
        Assert.Contains("not a valid absolute URI", query["error_description"]);
    }

    [Fact]
    public async Task Authorize_ResourceWithFragment_RedirectsWithInvalidTarget()
    {
        var url = BuildAuthorizeUrl(resources: ["https://api.test/v1#frag"]);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("invalid_target", query["error"]);
    }

    [Fact]
    public async Task Authorize_ResourceNotRegistered_RedirectsWithInvalidTarget()
    {
        var url = BuildAuthorizeUrl(resources: ["https://evil.example.com/api"]);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("invalid_target", query["error"]);
        Assert.Contains("not registered for this client", query["error_description"]);
    }

    [Fact]
    public async Task Authorize_ValidRegisteredResource_RedirectsWithCode()
    {
        var url = BuildAuthorizeUrl(resources: ["https://api.test/v1"]);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://app.test/callback", location);
        Assert.Contains("code=", location);
    }

    [Fact]
    public async Task Authorize_MultipleValidResources_RedirectsWithCode()
    {
        var url = BuildAuthorizeUrl(resources: ["https://api.test/v1", "https://api.test/v2"]);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("code=", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Authorize_ResourceRejectionPreservesState()
    {
        var url = BuildAuthorizeUrl(resources: ["https://evil.example.com/api"], state: "user-state-42");

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("user-state-42", query["state"]);
    }

    // -----------------------------------------------------------------------
    // Access token aud claim narrowing (end-to-end)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AccessToken_NoResource_UsesClientAudiences()
    {
        var token = await RunAuthorizationFlow(resources: null);
        var aud = ReadAudClaim(token);

        // With no resource param, both client audiences should be in the aud claim
        Assert.Contains("https://api.test/v1", aud);
        Assert.Contains("https://api.test/v2", aud);
    }

    [Fact]
    public async Task AccessToken_SingleResource_NarrowsAudToThatResource()
    {
        var token = await RunAuthorizationFlow(resources: ["https://api.test/v1"]);
        var aud = ReadAudClaim(token);

        Assert.Contains("https://api.test/v1", aud);
        Assert.DoesNotContain("https://api.test/v2", aud);
    }

    [Fact]
    public async Task AccessToken_MultipleResources_IncludesAllRequestedResources()
    {
        var token = await RunAuthorizationFlow(resources: ["https://api.test/v1", "https://api.test/v2"]);
        var aud = ReadAudClaim(token);

        Assert.Contains("https://api.test/v1", aud);
        Assert.Contains("https://api.test/v2", aud);
    }

    // -----------------------------------------------------------------------
    // Refresh token resource-subset validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshToken_ResourceSubset_Succeeds()
    {
        var (_, refreshToken) = await RunAuthorizationFlowReturningBothTokens(
            scope: "openid offline_access",
            resources: ["https://api.test/v1", "https://api.test/v2"]);

        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["resource"] = "https://api.test/v1"
        });

        var response = await _client.PostAsync("/connect/token", refreshForm);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aud = ReadAudClaim(tokens.GetProperty("access_token").GetString()!);
        Assert.Contains("https://api.test/v1", aud);
        Assert.DoesNotContain("https://api.test/v2", aud);
    }

    [Fact]
    public async Task RefreshToken_ResourceNotInOriginalGrant_ReturnsInvalidTarget()
    {
        var (_, refreshToken) = await RunAuthorizationFlowReturningBothTokens(
            scope: "openid offline_access",
            resources: ["https://api.test/v1"]);

        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["resource"] = "https://api.test/v2"
        });

        var response = await _client.PostAsync("/connect/token", refreshForm);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_target", error.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> RunAuthorizationFlow(
        string[]? resources = null,
        string scope = "openid profile email")
    {
        var (accessToken, _) = await RunAuthorizationFlowReturningBothTokens(scope, resources);
        return accessToken;
    }

    private async Task<(string AccessToken, string RefreshToken)> RunAuthorizationFlowReturningBothTokens(
        string scope,
        string[]? resources)
    {
        var (codeVerifier, codeChallenge) = GeneratePkce();
        var authorizeUrl = BuildAuthorizeUrl(scope: scope, codeChallenge: codeChallenge, resources: resources);

        var authorizeResponse = await _client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);

        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]
            ?? throw new InvalidOperationException("authorize response did not include a code: " + authorizeResponse.Headers.Location);

        var tokenFields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = codeVerifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        };

        var tokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(tokenFields));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        return (accessToken, refreshToken);
    }

    [Fact]
    public async Task Authorize_UnauthIdpHint_RedirectsToFederationLogin()
    {
        // Fresh client — no cookie. idp_hint should route the unauth request
        // through /oidc/{hint}/login rather than challenging the cookie scheme.
        using var freshClient = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var url = BuildAuthorizeUrl() + "&idp_hint=guest-share-link";
        var response = await freshClient.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/oidc/guest-share-link/login", location);
        Assert.Contains("returnUrl=", location);
        // returnUrl preserves the original /authorize URL so the user lands
        // back at this endpoint with their requested scope after federation.
        Assert.Contains(Uri.EscapeDataString("/connect/authorize"), location);
    }

    [Fact]
    public async Task Authorize_UnauthNoIdpHint_ChallengesAuthScheme()
    {
        using var freshClient = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var url = BuildAuthorizeUrl();
        var response = await freshClient.GetAsync(url);

        // Cookie scheme challenge — typically a 302 to the host's login UI.
        // Just assert it's not redirecting to /oidc/, which would mean the
        // hint path fired without a hint being supplied.
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain("/oidc/", location);
    }

    private static string[] ReadAudClaim(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);
        return token.Audiences.ToArray();
    }

    private static string BuildAuthorizeUrl(
        string scope = "openid profile email",
        string? codeChallenge = null,
        string[]? resources = null,
        string state = "test-state-123")
    {
        codeChallenge ??= GeneratePkce().Challenge;

        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
                  $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
                  $"&response_type=code" +
                  $"&scope={Uri.EscapeDataString(scope)}" +
                  $"&state={Uri.EscapeDataString(state)}" +
                  $"&code_challenge={codeChallenge}" +
                  $"&code_challenge_method=S256";

        if (resources is not null)
        {
            foreach (var r in resources)
                url += $"&resource={Uri.EscapeDataString(r)}";
        }

        return url;
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
