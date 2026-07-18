using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

[Collection("Azurite")]
public sealed class OidcSsoEndpointTests : IAsyncLifetime
{
    private readonly OidcMockHandler _oidcMock = new();
    private readonly AuthagonalTestFactory _factory;
    private HttpClient _client = null!;
    private string _connectionId = null!;
    private string _adminToken = null!;

    public OidcSsoEndpointTests(AzuriteFixture azurite)
    {
        _factory = new AuthagonalTestFactory
        {
            OidcHttpHandler = _oidcMock,
            AzuriteConnectionString = azurite.ConnectionString
        };
    }

    public async Task InitializeAsync()
    {
        // Mock IdP asserts an address within the connection's allowed domain (configured below).
        _oidcMock.Email = "oidcuser@oidctest.com";
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);

        _connectionId = await CreateConnectionAsync(["oidctest.com"]);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // Creates an OIDC connection (optionally with AllowedDomains) and returns its connectionId.
    private async Task<string> CreateConnectionAsync(string[]? allowedDomains)
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Test OIDC IdP",
                metadataLocation = $"{_oidcMock.Issuer}/.well-known/openid-configuration",
                clientId = "test-oidc-client",
                clientSecret = "test-oidc-secret",
                redirectUrl = $"{AuthagonalTestFactory.TestIssuer}/oidc/callback",
                allowedDomains = allowedDomains ?? Array.Empty<string>(),
                jitProvisioningEnabled = true
            }));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("connectionId").GetString()!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task OidcLogin_RedirectsToIdp()
    {
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        Assert.Contains("oidc-idp.test/authorize", location);
        Assert.Contains("client_id=test-oidc-client", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("code_challenge=", location);
        Assert.Contains("state=", location);
    }

    [Fact]
    public async Task OidcLogin_WithReturnUrl_StoresIt()
    {
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl=/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // State is stored with returnUrl — we can't inspect it directly but the redirect should work
    }

    [Fact]
    public async Task OidcLogin_InvalidConnection_Returns404()
    {
        var response = await _client.GetAsync("/oidc/nonexistent/login");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OidcCallback_MissingCode_Returns400()
    {
        var response = await _client.GetAsync("/oidc/callback?state=invalid");
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OidcCallback_InvalidState_Returns400()
    {
        var response = await _client.GetAsync("/oidc/callback?code=test&state=invalid-state");
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OidcCallback_IdpError_RedirectsWithError()
    {
        var response = await _client.GetAsync("/oidc/callback?error=access_denied&error_description=User+cancelled");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=oidc_error", location);
    }

    [Fact]
    public async Task OidcCallback_FullFlow_CreatesUserAndRedirects()
    {
        // Step 1: Initiate login to get state stored
        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        // Extract state and nonce from the redirect URL
        var authorizeUrl = loginResponse.Headers.Location!.ToString();
        var queryString = new Uri(authorizeUrl).Query;
        var qs = HttpUtility.ParseQueryString(queryString);
        var state = qs["state"]!;
        var nonce = qs["nonce"]!;

        // Set the nonce on the mock so the ID token matches
        _oidcMock.Nonce = nonce;

        // Step 2: Simulate IdP callback with the code and state
        var callbackResponse = await _client.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        // Should redirect to returnUrl (or login page on success)
        Assert.True(
            callbackResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"Expected redirect or OK, got {callbackResponse.StatusCode}");

        // Verify user was created
        var user = await _factory.UserStore.FindByEmailAsync(_oidcMock.Email);
        Assert.NotNull(user);
        Assert.Equal(_oidcMock.Email, user.Email);
    }

    [Fact]
    public async Task OidcCallback_ExistingUser_LinksExternalLogin()
    {
        // Pre-create the user
        await _factory.SeedTestUserAsync(email: _oidcMock.Email);

        // Run the full OIDC flow
        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login");
        var authorizeUrl = loginResponse.Headers.Location!.ToString();
        var qs = HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query);
        var state = qs["state"]!;
        _oidcMock.Nonce = qs["nonce"]!;

        var callbackResponse = await _client.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        Assert.True(
            callbackResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"Expected redirect or OK, got {callbackResponse.StatusCode}");

        // Verify external login was linked
        var logins = await _factory.UserStore.GetLoginsAsync(
            (await _factory.UserStore.FindByEmailAsync(_oidcMock.Email))!.Id);
        Assert.Contains(logins, l => l.Provider.StartsWith("oidc:"));
    }

    [Fact]
    public async Task OidcCallback_DomainAuthorised_UnverifiedEmail_ExistingUser_Links()
    {
        // F36: matching SAML, the connection's AllowedDomains vouch is the trust anchor for attaching to a
        // pre-existing account. When the domain IS authorised, the IdP's assertion links even if the
        // upstream doesn't flag the email verified — the admin has explicitly trusted this IdP for the domain.
        _oidcMock.EmailVerified = false;
        await _factory.SeedTestUserAsync(email: _oidcMock.Email);

        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login");
        var qs = HttpUtility.ParseQueryString(new Uri(loginResponse.Headers.Location!.ToString()).Query);
        _oidcMock.Nonce = qs["nonce"]!;
        var state = qs["state"]!;

        await _client.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        var user = await _factory.UserStore.FindByEmailAsync(_oidcMock.Email);
        var logins = await _factory.UserStore.GetLoginsAsync(user!.Id);
        Assert.Contains(logins, l => l.Provider.StartsWith("oidc:"));
    }

    [Fact]
    public async Task OidcCallback_NotDomainAuthorised_ExistingUser_NotLinked()
    {
        // F36: a connection NOT authorised for the email's domain (empty AllowedDomains) must refuse to
        // attach to a pre-existing local account even on a VERIFIED email — email_verified alone is an
        // upstream-controlled boolean and can't be allowed to seize an existing (possibly admin) account.
        var conn = await CreateConnectionAsync(allowedDomains: null);
        _oidcMock.EmailVerified = true;
        await _factory.SeedTestUserAsync(email: _oidcMock.Email);

        var loginResponse = await _client.GetAsync($"/oidc/{conn}/login");
        var qs = HttpUtility.ParseQueryString(new Uri(loginResponse.Headers.Location!.ToString()).Query);
        _oidcMock.Nonce = qs["nonce"]!;
        var state = qs["state"]!;

        var cb = await _client.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        // Rejected with access_denied, and no external login attached.
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Contains("access_denied", cb.Headers.Location!.ToString());
        var user = await _factory.UserStore.FindByEmailAsync(_oidcMock.Email);
        var logins = await _factory.UserStore.GetLoginsAsync(user!.Id);
        Assert.DoesNotContain(logins, l => l.Provider.StartsWith("oidc:"));
    }

    [Fact]
    public async Task OidcCallback_TokenExchangeFails_RedirectsWithError()
    {
        _oidcMock.FailTokenExchange = true;

        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login");
        var authorizeUrl = loginResponse.Headers.Location!.ToString();
        var state = HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;

        var callbackResponse = await _client.GetAsync($"/oidc/callback?code=bad-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        var location = callbackResponse.Headers.Location!.ToString();
        Assert.Contains("error", location);
    }

    [Fact]
    public async Task OidcCallback_MfaEnrolledUser_RoutesThroughMfaChallenge()
    {
        // F42: a federated login for an MFA-enrolled user is only the FIRST factor — the callback must
        // redirect to the local MFA challenge, not sign a fully-authenticated session.
        var user = await _factory.SeedTestUserAsync(email: _oidcMock.Email);
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "TOTP",
            SecretProtected = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login");
        var qs = HttpUtility.ParseQueryString(new Uri(loginResponse.Headers.Location!.ToString()).Query);
        _oidcMock.Nonce = qs["nonce"]!;
        var state = qs["state"]!;

        var cb = await _client.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        var location = cb.Headers.Location!.ToString();
        Assert.Contains("/mfa-challenge", location);
        Assert.Contains("challengeId=", location);
    }

    [Fact]
    public async Task OidcCallback_MissingStateCookie_RejectedAsLoginCsrf()
    {
        // F48d: a callback delivered to a DIFFERENT browser (one that never got the oidc_state binding
        // cookie) must be rejected even with an otherwise-valid state — the login-CSRF defense.
        var loginResponse = await _client.GetAsync($"/oidc/{_connectionId}/login");
        var qs = HttpUtility.ParseQueryString(new Uri(loginResponse.Headers.Location!.ToString()).Query);
        var state = qs["state"]!;

        var otherBrowser = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var cb = await otherBrowser.GetAsync($"/oidc/callback?code=test-auth-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.BadRequest, cb.StatusCode);
        var json = await cb.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_state", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SsoCheck_OidcDomain_ReturnsSsoRequired()
    {
        var response = await _client.GetAsync("/api/auth/sso-check?email=user@oidctest.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ssoRequired").GetBoolean());
    }

    [Fact]
    public async Task OidcLogin_ForwardsStandardScopes_DropsCustom()
    {
        // F40: only STANDARD OIDC scopes ride to the upstream — the downstream RP's custom API scope
        // (projects-api.read) is dropped, since a strict IdP like Google 400s invalid_scope on it and the
        // upstream only needs to identify the user. profile/email are forwarded because they're standard.
        var originalAuthorize = "/connect/authorize?client_id=foo&scope=openid+profile+email+projects-api.read&response_type=code";
        var encoded = Uri.EscapeDataString(originalAuthorize);
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl={encoded}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var qs = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        var scopes = qs["scope"]!.Split(' ');
        Assert.Contains("openid", scopes);
        Assert.Contains("profile", scopes);
        Assert.Contains("email", scopes);
        Assert.DoesNotContain("projects-api.read", scopes);
    }

    [Fact]
    public async Task OidcLogin_NoReturnUrlScope_FallsBackToBaseline()
    {
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl=/dashboard");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var qs = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("openid profile email", qs["scope"]);
    }

    [Fact]
    public async Task OidcLogin_ForwardedScope_AlwaysIncludesOpenid()
    {
        // If the downstream RP asked only for a custom scope (no standard ones), the upstream still needs
        // openid to mint an id_token we can validate — it's added back, and the custom scope is dropped (F40).
        var originalAuthorize = "/connect/authorize?client_id=foo&scope=projects-api.read&response_type=code";
        var encoded = Uri.EscapeDataString(originalAuthorize);
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl={encoded}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var qs = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        var scopes = qs["scope"]!.Split(' ');
        Assert.Contains("openid", scopes);
        Assert.DoesNotContain("projects-api.read", scopes);
    }

    [Fact]
    public async Task OidcLogin_ForwardsWhitelistedPassthroughParam()
    {
        // Configure a separate connection with link_token in PassthroughParams.
        // Admin API doesn't expose PUT for OIDC connections so we create a fresh one.
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Test OIDC IdP With Passthrough",
                metadataLocation = $"{_oidcMock.Issuer}/.well-known/openid-configuration",
                clientId = "test-oidc-client",
                clientSecret = "test-oidc-secret",
                redirectUrl = $"{AuthagonalTestFactory.TestIssuer}/oidc/callback",
                allowedDomains = Array.Empty<string>(),
                passthroughParams = new[] { "link_token" }
            }));
        createResponse.EnsureSuccessStatusCode();
        var passthroughConnId = (await createResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId").GetString()!;

        // Original /authorize URL carries link_token=tok-123. /oidc/login should
        // pull it off the returnUrl and forward to the upstream authorize URL.
        var originalAuthorize = "/connect/authorize?client_id=foo&scope=openid&link_token=tok-123";
        var encoded = Uri.EscapeDataString(originalAuthorize);
        var response = await _client.GetAsync($"/oidc/{passthroughConnId}/login?returnUrl={encoded}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var qs = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Equal("tok-123", qs["link_token"]);
    }

    [Fact]
    public async Task OidcLogin_DropsNonWhitelistedParams()
    {
        // Default connection has no passthroughParams. An incoming `link_token`
        // should NOT reach the upstream URL — only the whitelist flows through.
        var originalAuthorize = "/connect/authorize?client_id=foo&scope=openid&link_token=secret-leak";
        var encoded = Uri.EscapeDataString(originalAuthorize);
        var response = await _client.GetAsync($"/oidc/{_connectionId}/login?returnUrl={encoded}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var qs = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        Assert.Null(qs["link_token"]);
    }
}
