using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class AuthEndpointTests : IAsyncLifetime
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
    // POST /api/auth/login
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithUserId()
    {
        var user = await _factory.SeedTestUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, json.GetProperty("userId").GetString());
        Assert.Equal("test@example.com", json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Login_ValidCredentials_SetsCookie()
    {
        await _factory.SeedTestUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        Assert.True(response.Headers.Contains("Set-Cookie"), "Response should set an auth cookie");
    }

    [Fact]
    public async Task Login_InvalidPassword_Returns401()
    {
        await _factory.SeedTestUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "WrongPassword1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_NonexistentUser_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "Test1234!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_MissingEmail_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { password = "Test1234!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("email_required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_MissingPassword_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("password_required", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_UnconfirmedEmail_Returns403()
    {
        await _factory.SeedTestUserAsync(email: "unconfirmed@example.com", emailConfirmed: false);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "unconfirmed@example.com", password = "Test1234!" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("email_not_confirmed", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_SsoDomain_Returns409WithRedirect()
    {
        await _factory.SsoDomainStore.UpsertAsync(new Core.Models.SsoDomain
        {
            Domain = "corp.com",
            ProviderType = "oidc",
            ConnectionId = "azure",
            Scheme = "oidc"
        });

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "user@corp.com", password = "Test1234!" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sso_required", json.GetProperty("error").GetString());
        Assert.Equal("/oidc/azure/login", json.GetProperty("redirectUrl").GetString());
    }

    [Fact]
    public async Task Login_Lockout_AfterMaxFailedAttempts()
    {
        await _factory.SeedTestUserAsync();

        // 5 failed attempts
        for (int i = 0; i < 5; i++)
            await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Wrong!" });

        // 6th attempt should return locked_out
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Wrong!" });

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("locked_out", json.GetProperty("error").GetString());
        Assert.True(json.GetProperty("retryAfter").GetInt32() > 0);
    }

    [Fact]
    public async Task Login_FiresAuthHook()
    {
        await _factory.SeedTestUserAsync();

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        Assert.Single(_factory.AuthHook.Authentications);
        Assert.Equal("password", _factory.AuthHook.Authentications[0].Method);
    }

    [Fact]
    public async Task Login_FailedLogin_FiresAuthHookFailure()
    {
        await _factory.SeedTestUserAsync();

        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Wrong!" });

        Assert.Single(_factory.AuthHook.LoginFailures);
        Assert.Equal("invalid_password", _factory.AuthHook.LoginFailures[0].Reason);
    }

    // -----------------------------------------------------------------------
    // GET /api/auth/session
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Session_Authenticated_ReturnsUserInfo()
    {
        var user = await _factory.SeedTestUserAsync();

        // Login first to get cookie
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.Equal(user.Id, json.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Session_NotAuthenticated_Returns401Or302()
    {
        var response = await _client.GetAsync("/api/auth/session");

        // Without auth the cookie middleware redirects to /login
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect,
            $"Expected 401 or 302, got {response.StatusCode}");
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/logout
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Logout_Authenticated_ReturnsSuccess()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // GET /api/auth/sso-check
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SsoCheck_NoSsoDomain_ReturnsFalse()
    {
        var response = await _client.GetAsync("/api/auth/sso-check?email=user@normal.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("ssoRequired").GetBoolean());
    }

    [Fact]
    public async Task SsoCheck_SsoDomain_ReturnsTrueWithRedirect()
    {
        await _factory.SsoDomainStore.UpsertAsync(new Core.Models.SsoDomain
        {
            Domain = "corp.com", ProviderType = "saml", ConnectionId = "okta", Scheme = "saml"
        });

        var response = await _client.GetAsync("/api/auth/sso-check?email=user@corp.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ssoRequired").GetBoolean());
        Assert.Equal("saml", json.GetProperty("providerType").GetString());
        Assert.Equal("/saml/okta/login", json.GetProperty("redirectUrl").GetString());
    }

    [Fact]
    public async Task SsoCheck_MissingEmail_Returns400()
    {
        var response = await _client.GetAsync("/api/auth/sso-check");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // GET /api/auth/providers
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Providers_ReturnsConfiguredProviders()
    {
        await _factory.OidcProviderStore.UpsertAsync(new Core.Models.OidcProviderConfig
        {
            ConnectionId = "google",
            ConnectionName = "Google",
            MetadataLocation = "https://accounts.google.com/.well-known/openid-configuration",
            ClientId = "google-client",
            ClientSecret = "secret",
            RedirectUrl = "https://test.local/oidc/callback"
        });

        var response = await _client.GetAsync("/api/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var providers = json.GetProperty("providers").EnumerateArray().ToList();
        Assert.Single(providers);
        Assert.Equal("google", providers[0].GetProperty("connectionId").GetString());
        Assert.Equal("/oidc/google/login", providers[0].GetProperty("loginUrl").GetString());
    }

    // -----------------------------------------------------------------------
    // GET /api/auth/password-policy
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PasswordPolicy_ReturnsRules()
    {
        var response = await _client.GetAsync("/api/auth/password-policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rules = json.GetProperty("rules").EnumerateArray().ToList();
        Assert.True(rules.Count > 0, "Should return at least one rule");
        Assert.Contains(rules, r => r.GetProperty("rule").GetString() == "minLength");
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/forgot-password
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ForgotPassword_ExistingUser_SendsEmail()
    {
        await _factory.SeedTestUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "test@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.EmailService.SentEmails, e => e.Email == "test@example.com" && e.Type == "password_reset");
    }

    [Fact]
    public async Task ForgotPassword_NonexistentUser_StillReturnsOk()
    {
        // Prevents email enumeration
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/reset-password
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResetPassword_ValidToken_ResetsPassword()
    {
        var user = await _factory.SeedTestUserAsync();

        // Simulate forgot-password flow to get a valid token
        await _client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "test@example.com" });
        var sentEmail = _factory.EmailService.SentEmails.Last();
        var uri = new Uri(sentEmail.CallbackUrl);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var token = queryParams["p"]!;

        var response = await _client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "NewPass1234!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify new password works
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "NewPass1234!" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_InvalidToken_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/reset-password", new { token = "bogus", newPassword = "NewPass1234!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WeakPassword_Returns400()
    {
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "test@example.com" });
        var sentEmail = _factory.EmailService.SentEmails.Last();
        var uri = new Uri(sentEmail.CallbackUrl);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var token = queryParams["p"]!;

        var response = await _client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "weak" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("weak_password", json.GetProperty("error").GetString());
    }
}
