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

    [Fact]
    public async Task ResetPassword_ConfirmsUnverifiedEmail()
    {
        // A user who registered, never verified, then resets their password: completing the reset
        // (proof of email control) should also confirm the email — don't dead-end unverified users.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "reset-verify@example.com", password = "NewPass1234!" });
        var before = await _factory.UserStore.FindByEmailAsync("reset-verify@example.com");
        Assert.False(before!.EmailConfirmed);

        await _client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "reset-verify@example.com" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "password_reset" && e.Email == "reset-verify@example.com");
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(sent.CallbackUrl).Query)["p"]!;

        var reset = await _client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "AnotherPass1234!" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var after = await _factory.UserStore.FindByEmailAsync("reset-verify@example.com");
        Assert.True(after!.EmailConfirmed, "completing a password reset should confirm the email");
    }

    // -----------------------------------------------------------------------
    // GET/POST /api/auth/confirm-email  (email verification — CRITICAL PATH)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Register_ThenClickVerificationLink_ConfirmsEmail()
    {
        // Registration sends a verification email; the new user starts unconfirmed.
        var register = await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "verify-me@example.com", password = "NewPass1234!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var before = await _factory.UserStore.FindByEmailAsync("verify-me@example.com");
        Assert.False(before!.EmailConfirmed, "user should start unconfirmed");

        // Follow the verification link the way a user actually does: a GET on the clicked link.
        // (This guards the regression where the route was POST-only and a clicked link 404'd to a
        // blank SPA page, leaving the user unverified.)
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "verify-me@example.com");
        var confirm = await _client.GetAsync(new Uri(sent.CallbackUrl).PathAndQuery);

        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Contains("email_confirmed=1", confirm.Headers.Location?.ToString() ?? "");

        var after = await _factory.UserStore.FindByEmailAsync("verify-me@example.com");
        Assert.True(after!.EmailConfirmed, "clicking the verification link should confirm the email");
    }

    [Fact]
    public async Task ConfirmEmail_PostWithJsonToken_ConfirmsAndReturnsJson()
    {
        // The programmatic (custom-login-UI) path posts the token as JSON and keeps the JSON contract.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "verify-post@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "verify-post@example.com");
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(sent.CallbackUrl).Query)["token"]!;

        var confirm = await _client.PostAsJsonAsync("/api/auth/confirm-email", new { token });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var after = await _factory.UserStore.FindByEmailAsync("verify-post@example.com");
        Assert.True(after!.EmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_GetWithExpiredOrBadToken_DoesNotConfirm()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "verify-bad@example.com", password = "NewPass1234!" });

        // A clicked link with a garbage token must not confirm anyone.
        var confirm = await _client.GetAsync("/api/auth/confirm-email?token=not-a-real-token");
        Assert.NotEqual(HttpStatusCode.Redirect, confirm.StatusCode);

        var after = await _factory.UserStore.FindByEmailAsync("verify-bad@example.com");
        Assert.False(after!.EmailConfirmed);
    }

    [Fact]
    public async Task Register_PasswordlessAccountClaim_StagesCredentialUntilFreshEmailProof()
    {
        // Opt-in AllowPasswordlessAccountClaim: the account was born from an emailed link, and the
        // CLAIM must re-prove inbox control rather than inherit that proof — anyone who merely
        // knows the email could otherwise take the account over. The credential is staged and only
        // a fresh verification click promotes it.
        await using var factory = new AuthagonalTestFactory { ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "federated@example.com",
            NormalizedEmail = "FEDERATED@EXAMPLE.COM",
            PasswordHash = null, // no local credential
            EmailConfirmed = true, // email proven via the upstream/federation — NOT by this claimer
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "federated@example.com", password = "Claim1234!", firstName = "Grown", lastName = "Up" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Staged, NOT active: the claimed password must not work before the fresh click, the
        // account stays passwordless (still claimable — an attacker's claim can't lock it), and a
        // fresh verification email went out despite the stored EmailConfirmed.
        var claimed = await factory.UserStore.GetAsync(federated.Id);
        Assert.Null(claimed!.PasswordHash);
        Assert.NotNull(claimed.PendingPasswordHash);
        var earlyLogin = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "federated@example.com", password = "Claim1234!" });
        Assert.NotEqual(HttpStatusCode.OK, earlyLogin.StatusCode);
        var mail = factory.EmailService.SentEmails.Last(e => e.Email == "federated@example.com" && e.Type == "verification");

        // The click IS the proof: credential promoted, then login works.
        var token = System.Web.HttpUtility.ParseQueryString(new System.Uri(mail.CallbackUrl).Query)["token"];
        var confirm = await client.GetAsync($"/api/auth/confirm-email?token={System.Uri.EscapeDataString(token!)}");
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);

        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.NotNull(after!.PasswordHash);
        Assert.Null(after.PendingPasswordHash);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "federated@example.com", password = "Claim1234!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Register_PasswordlessAccount_WhenDisabled_StaysNeutral()
    {
        // Default (flag OFF): even a passwordless account is treated as a duplicate — no claim.
        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "fed-default@example.com",
            NormalizedEmail = "FED-DEFAULT@EXAMPLE.COM",
            PasswordHash = null,
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await _factory.UserStore.CreateAsync(federated);

        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "fed-default@example.com", password = "Claim1234!" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // neutral

        var stillPasswordless = await _factory.UserStore.GetAsync(federated.Id);
        Assert.True(string.IsNullOrEmpty(stillPasswordless!.PasswordHash));
    }

    [Fact]
    public async Task Register_ExistingPasswordedAccount_StaysNeutralNoTakeover()
    {
        // A real credentialed account must NEVER be overwritten by a re-register — the response is the
        // enumeration-neutral 201 and the original password still works (the new one does not).
        await _factory.SeedTestUserAsync(email: "real@example.com", password: "Original1!");

        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "real@example.com", password = "Attacker9!" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // neutral

        var withNew = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "real@example.com", password = "Attacker9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, withNew.StatusCode);
        var withOriginal = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "real@example.com", password = "Original1!" });
        Assert.Equal(HttpStatusCode.OK, withOriginal.StatusCode);
    }

}
