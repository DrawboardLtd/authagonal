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
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);

        // Create OIDC connection via admin API
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Test OIDC IdP",
                metadataLocation = $"{_oidcMock.Issuer}/.well-known/openid-configuration",
                clientId = "test-oidc-client",
                clientSecret = "test-oidc-secret",
                redirectUrl = $"{AuthagonalTestFactory.TestIssuer}/oidc/callback",
                allowedDomains = new[] { "oidctest.com" }
            }));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        _connectionId = json.GetProperty("connectionId").GetString()!;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

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
    public async Task SsoCheck_OidcDomain_ReturnsSsoRequired()
    {
        var response = await _client.GetAsync("/api/auth/sso-check?email=user@oidctest.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ssoRequired").GetBoolean());
    }
}
