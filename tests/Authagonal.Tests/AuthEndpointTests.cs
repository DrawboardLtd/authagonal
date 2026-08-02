using System.Diagnostics;
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
    public async Task Login_Lockout_WithCorrectPassword_ReportsLockedOut()
    {
        await _factory.SeedTestUserAsync();

        for (int i = 0; i < 6; i++)
            await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Wrong!" });

        // A caller who proves the password is told why they are blocked — that is the useful part of
        // the 423, and it discloses nothing they did not already know.
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("locked_out", json.GetProperty("error").GetString());
        Assert.True(json.GetProperty("retryAfter").GetInt32() > 0);
    }

    [Fact]
    public async Task Login_Lockout_WithWrongPassword_IsIndistinguishableFromAnUnknownAccount()
    {
        // This test previously asserted the opposite: that a sixth WRONG password produced 423. That
        // made lockout a definitive account-existence oracle — six guesses against a real address
        // eventually answered locked_out, while the same guesses against an address with no account
        // answered invalid_credentials forever. Since every account-creation path sets LockoutEnabled,
        // it covered the whole directory, and it undid the dummy-hash timing equalisation, the neutral
        // duplicate registration and the randomised forgot-password delay on the same surface.
        // The test also used to stop at the status code and the body, which is only half of
        // "indistinguishable" and was the half that already held. The locked-out branch returns after
        // one lookup and one verify with no lockout write and no audit hook, and it did not pay the
        // wall-clock floor every other invalid_credentials pays — so the two responses were byte-
        // identical and 100-250ms apart at the shipped PBKDF2 cost. An attacker picks who is in that
        // state (only an existing account can be locked out), so it was the enumeration oracle in
        // full: six wrong guesses to trip the lockout, then time the seventh.
        await _factory.SeedTestUserAsync();

        for (int i = 0; i < 6; i++)
            await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Wrong!" });

        var lockedClock = Stopwatch.StartNew();
        var real = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Wrong!" });
        lockedClock.Stop();

        var unknownClock = Stopwatch.StartNew();
        var unknown = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "no-such-account@example.com", password = "Wrong!" });
        unknownClock.Stop();

        Assert.Equal(HttpStatusCode.Unauthorized, real.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            await real.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());

        // Same floor the sibling timing test uses. It is a lower bound the handler waits out, so a
        // response can be slow but never fast; the tolerance covers timer granularity only.
        var floor = TimeSpan.FromMilliseconds(new Authagonal.Server.Services.AuthOptions().FailedLoginMinimumMilliseconds - 40);

        Assert.True(lockedClock.Elapsed >= floor,
            $"Wrong password against a LOCKED OUT account returned in {lockedClock.Elapsed.TotalMilliseconds:F0}ms, "
            + $"under the {floor.TotalMilliseconds:F0}ms floor — the identical body is undone by the clock.");
        Assert.True(unknownClock.Elapsed >= floor,
            $"Login for a nonexistent account returned in {unknownClock.Elapsed.TotalMilliseconds:F0}ms, "
            + $"under the {floor.TotalMilliseconds:F0}ms floor.");
    }

    [Fact]
    public async Task Login_FailedAttempts_AreHeldToTheSameWallClockFloor()
    {
        // The no-such-user path verifies against a dummy hash so it isn't free, but the dummy is
        // always the native PBKDF2 format at the configured cost while a real account may hold a
        // bcrypt or ASP.NET Identity V3 hash at a different one — which turns response latency into
        // an account-existence oracle. Both failures must instead leave at the same fixed deadline.
        await _factory.SeedTestUserAsync();

        async Task<TimeSpan> TimeFailedLoginAsync(string email)
        {
            var started = Stopwatch.StartNew();
            var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = "WrongPassword1!" });
            started.Stop();
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            return started.Elapsed;
        }

        // The floor is a lower bound the handler waits out, so this can be slow but never fast; the
        // tolerance covers timer granularity only.
        var floor = TimeSpan.FromMilliseconds(new Authagonal.Server.Services.AuthOptions().FailedLoginMinimumMilliseconds - 40);

        var existing = await TimeFailedLoginAsync("test@example.com");
        var nonexistent = await TimeFailedLoginAsync("nobody@example.com");

        Assert.True(existing >= floor, $"Wrong password on an existing account returned in {existing.TotalMilliseconds:F0}ms, under the {floor.TotalMilliseconds:F0}ms floor");
        Assert.True(nonexistent >= floor, $"Login for a nonexistent account returned in {nonexistent.TotalMilliseconds:F0}ms, under the {floor.TotalMilliseconds:F0}ms floor");
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
    // F307 — an anonymous registrant must not be able to write control
    // characters into a log line, a storage key or an index row
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("victim\r\n2026-01-01 INFO User registered: admin@example.com")] // forged log entry
    [InlineData("victim\ttabbed")]
    [InlineData("victim\u0000null")]
    public async Task Register_WithControlCharactersInTheAddress_IsRefused(string localPart)
    {
        // The address reaches "User registered: {UserId} in domain {Domain}" and, before that, the
        // email index key. A CR/LF in it forges whole entries in any line-oriented sink, which is how
        // the rest of an attacker's activity gets hidden. SCIM refuses the same characters
        // (ScimUserEndpoints.IsPlausibleEmail); self-registration needs no credential at all and did not.
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { email = $"{localPart}@example.com", password = "NewPass1234!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_email", json.GetProperty("error").GetString());
        Assert.Null(await _factory.UserStore.FindByEmailAsync($"{localPart}@example.com"));
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

        // Follow the link the way a user actually does: GET renders the confirm page, the button
        // posts the form. The GET must not confirm on its own — see ConfirmEmail_Get_DoesNotConfirm.
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "verify-me@example.com");
        var confirm = await ClickAndConfirmAsync(sent.CallbackUrl);

        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Contains("email_confirmed=1", confirm.Headers.Location?.ToString() ?? "");

        var after = await _factory.UserStore.FindByEmailAsync("verify-me@example.com");
        Assert.True(after!.EmailConfirmed, "clicking the verification link should confirm the email");
    }

    [Fact]
    public async Task Register_WithReturnUrl_VerificationLink_ThreadsItToTheLoginLanding()
    {
        // A registration that began MID-JOURNEY (e.g. an invite-accept continuation) must resume
        // that journey after the email click: the returnUrl rides the verification token and is
        // re-emitted on the login landing, where the login page (and its MFA "Not now" skip)
        // honors it via resolveRedirect. Without this the email hop ate the returnUrl and the
        // user stranded on the account card with the invite never redeemed.
        var journey = "https://app.example.com/workspaces/join/abc123?x=1";
        var register = await _client.PostAsJsonAsync(
            $"/api/auth/register?returnUrl={Uri.EscapeDataString(journey)}",
            new { email = "verify-journey@example.com", password = "NewPass1234!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "verify-journey@example.com");
        var confirm = await ClickAndConfirmAsync(sent.CallbackUrl);

        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var landing = confirm.Headers.Location?.ToString() ?? "";
        Assert.Contains("email_confirmed=1", landing);
        var landingReturnUrl = System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("https://x"), landing).Query)["returnUrl"];
        Assert.Equal(journey, landingReturnUrl);
    }

    [Fact]
    public async Task Register_WithoutReturnUrl_VerificationLanding_HasNoReturnUrl()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "verify-plain@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "verify-plain@example.com");
        var confirm = await ClickAndConfirmAsync(sent.CallbackUrl);

        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.DoesNotContain("returnUrl=", confirm.Headers.Location?.ToString() ?? "");
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


    /// <summary>Drives the confirmation the way a human now does: GET the page, then post the form.</summary>
    private async Task<HttpResponseMessage> ClickAndConfirmAsync(string callbackUrl)
    {
        var pathAndQuery = new Uri(callbackUrl).PathAndQuery;
        var page = await _client.GetAsync(pathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var token = System.Web.HttpUtility.ParseQueryString(new Uri(callbackUrl).Query)["token"]!;
        return await _client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));
    }


    [Fact]
    public async Task ConfirmEmail_Get_DoesNotConfirm_SoScannersCannotBurnTheLink()
    {
        // THE regression. Mail security products (Defender Safe Links, Proofpoint, Mimecast), link
        // prefetchers and chat unfurlers all GET the URLs in inbound mail. While GET performed the
        // confirmation, those fetches consumed the single-use token and the real user landed on
        // "this link has already been used" — worst of all for enterprise customers, who run exactly
        // that tooling.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "scanner@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "scanner@example.com");

        // A scanner fetches the link, twice for good measure.
        await _client.GetAsync(new Uri(sent.CallbackUrl).PathAndQuery);
        var page = await _client.GetAsync(new Uri(sent.CallbackUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType?.ToString() ?? "");

        var afterScan = await _factory.UserStore.FindByEmailAsync("scanner@example.com");
        Assert.False(afterScan!.EmailConfirmed, "a GET must never confirm — that is what burned the token");

        // The human then clicks the button and it still works.
        var confirm = await ClickAndConfirmAsync(sent.CallbackUrl);
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var after = await _factory.UserStore.FindByEmailAsync("scanner@example.com");
        Assert.True(after!.EmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_ReplayedAfterSuccess_ReportsSuccessNotAnError()
    {
        // Double-click, back button, a second device opening the same mail. The link asserts "this
        // address is verified", which stays true, so a replay must not read as a failure.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "replay@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "replay@example.com");

        var first = await ClickAndConfirmAsync(sent.CallbackUrl);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        var token = System.Web.HttpUtility.ParseQueryString(new Uri(sent.CallbackUrl).Query)["token"]!;

        var replayForm = await _client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));
        Assert.Equal(HttpStatusCode.Redirect, replayForm.StatusCode);
        Assert.Contains("email_confirmed=1", replayForm.Headers.Location?.ToString() ?? "");

        var replayJson = await _client.PostAsJsonAsync("/api/auth/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, replayJson.StatusCode);

        // The page renders the generic confirm prompt rather than reporting account state. Confirming
        // it reports "already confirmed" is what made this an anonymous existence oracle: the token is
        // unauthenticated and forgeable, so anyone could ask about any address. The button does not
        // fail — the POST above is what answers, and it answers correctly.
        var page = await _client.GetAsync(new Uri(sent.CallbackUrl).PathAndQuery);
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("Confirm your email", body);
        Assert.DoesNotContain("already confirmed", body);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ConfirmEmail_ReplayedOnUnconfirmedAccount_StillFails()
    {
        // The idempotency must not become a blanket "any stale token is fine". With nothing confirmed
        // there is no true assertion to stand behind, so it stays an error.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "stale@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "stale@example.com");
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(sent.CallbackUrl).Query)["token"]!;

        // Rotate the stamp without confirming, which is what any other credential change does.
        var user = await _factory.UserStore.FindByEmailAsync("stale@example.com");
        user!.SecurityStamp = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await _factory.UserStore.UpdateAsync(user);

        var replay = await _client.PostAsJsonAsync("/api/auth/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var after = await _factory.UserStore.FindByEmailAsync("stale@example.com");
        Assert.False(after!.EmailConfirmed);
    }


    [Fact]
    public async Task VerificationEmails_LinkToAnAnonymouslyClickableEndpoint()
    {
        // The email-change / resend-verification flows used to link at /api/v1/profile/confirm-email,
        // which is POST-only AND behind RequireAuthorization("IdentityAdmin"). A clicked link is an
        // anonymous GET, so that link could never work for anyone. Every verification email must point
        // somewhere a signed-out browser can actually reach.
        await _client.PostAsJsonAsync("/api/auth/register",
            new { email = "clickable@example.com", password = "NewPass1234!" });
        var sent = _factory.EmailService.SentEmails.Last(e => e.Type == "verification" && e.Email == "clickable@example.com");

        Assert.DoesNotContain("/api/v1/profile/confirm-email", sent.CallbackUrl);

        // Anonymous GET on the emailed URL renders, rather than 401/404/405.
        var page = await _client.GetAsync(new Uri(sent.CallbackUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
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
        // GET only renders the page now; the confirmation happens on the form post.
        var page = await client.GetAsync($"/api/auth/confirm-email?token={System.Uri.EscapeDataString(token!)}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token!)]));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);

        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.NotNull(after!.PasswordHash);
        Assert.Null(after.PendingPasswordHash);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "federated@example.com", password = "Claim1234!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>
    /// The claimed profile must actually reach the store, not be applied in memory and then dropped.
    /// </summary>
    /// <remarks>
    /// <c>ApplyPendingClaim</c> puts the staged first/last name and custom attributes on the in-memory user
    /// before the provisioning round-trip, so the downstream conversion sees the claim's signup context. The
    /// rebase then re-reads the row and copies only the credential fields across — so the claimed profile was
    /// dropped, and <c>PendingClaimJson</c> was nulled in the same breath, which makes it destroyed rather
    /// than deferred. Nothing could recover it afterwards.
    /// <para>
    /// The failure path already reloads a clean copy "so none of the staged profile/attributes applied above
    /// persist", which is only a meaningful precaution if the success path persists them. This asserts the
    /// half that was missing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PasswordlessClaim_PersistsTheClaimedProfile()
    {
        await using var factory = new AuthagonalTestFactory { ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "profile@example.com",
            NormalizedEmail = "PROFILE@EXAMPLE.COM",
            PasswordHash = null,
            EmailConfirmed = true,
            FirstName = "Placeholder",
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        await client.PostAsJsonAsync("/api/auth/register",
            new { email = "profile@example.com", password = "Claim1234!", firstName = "Ada", lastName = "Lovelace" });

        var mail = factory.EmailService.SentEmails.Last(e => e.Email == "profile@example.com" && e.Type == "verification");
        var token = System.Web.HttpUtility.ParseQueryString(new System.Uri(mail.CallbackUrl).Query)["token"];
        await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token!)]));

        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.Equal("Ada", after!.FirstName);
        Assert.Equal("Lovelace", after.LastName);
        // And the credential still promoted, so the profile fix did not displace the point of the claim.
        Assert.NotNull(after.PasswordHash);
        Assert.Null(after.PendingClaimJson);
    }

    /// <summary>
    /// A confirmation link must not vouch for an address it was not issued for.
    /// </summary>
    /// <remarks>
    /// The token proves control of ONE address — the one inside it — and the security-stamp check that
    /// authorises the request is made against the row as it was BEFORE the provisioning round-trip. Neither
    /// is re-evaluated by the re-read, so if an admin PATCH or a SCIM replace changed the account's address
    /// during the round-trip (nothing in the tree sets EmailConfirmed=false on an address change),
    /// <c>EmailConfirmed = true</c> was stamped onto an address nobody had proved, the email-confirmed hook
    /// fired with it, and <c>/connect/userinfo</c> then asserted <c>email_verified=true</c> for it.
    /// </remarks>
    [Fact]
    public async Task ConfirmEmail_DoesNotVouchForAnAddressChangedMidFlight()
    {
        await using var factory = new AuthagonalTestFactory { ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "old@corp.test",
            NormalizedEmail = "OLD@CORP.TEST",
            PasswordHash = null,
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        await client.PostAsJsonAsync("/api/auth/register",
            new { email = "old@corp.test", password = "Claim1234!" });

        var mail = factory.EmailService.SentEmails.Last(e => e.Email == "old@corp.test" && e.Type == "verification");
        var token = System.Web.HttpUtility.ParseQueryString(new System.Uri(mail.CallbackUrl).Query)["token"];

        // The address moves inside the provisioning round-trip — the window the rebase exists to survive.
        factory.Provisioning.DuringReprovision = async _ =>
        {
            var row = (await factory.UserStore.GetAsync(federated.Id))!;
            row.Email = "new@corp.test";
            row.NormalizedEmail = "NEW@CORP.TEST";
            await factory.UserStore.UpdateAsync(row);
        };

        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token!)]));

        // The new address is NOT vouched for, and the staged credential is not promoted onto it either.
        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.Equal("new@corp.test", after!.Email);
        Assert.Null(after.PasswordHash);
        Assert.NotEqual(HttpStatusCode.OK, confirm.StatusCode);
    }

    /// <summary>
    /// An account deleted while its confirmation is in flight must stay deleted.
    /// </summary>
    /// <remarks>
    /// The rebase read the row back with <c>?? user</c>, and <c>GetAsync</c> returns null only when the row
    /// is gone — so a delete landing during the provisioning round-trip (SCIM deprovision on offboarding, an
    /// admin delete, a GDPR erasure) made the handler fall back to writing the pre-round-trip instance. That
    /// is the exact stale write the rebase exists to eliminate, and worse: <c>UpdateAsync</c> CREATES the row
    /// when it is absent on all three persistent providers, so the erased account came back with
    /// <c>EmailConfirmed = true</c> and the claimant's promoted password, with no concurrency guard consulted
    /// and nothing logged. Erasing an account while its owner had a verification link open un-erased it.
    /// </remarks>
    [Fact]
    public async Task ConfirmEmail_DoesNotResurrectAnAccountDeletedMidFlight()
    {
        await using var factory = new AuthagonalTestFactory { ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "erased@example.com",
            NormalizedEmail = "ERASED@EXAMPLE.COM",
            PasswordHash = null,
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        await client.PostAsJsonAsync("/api/auth/register",
            new { email = "erased@example.com", password = "Claim1234!" });

        var mail = factory.EmailService.SentEmails.Last(e => e.Email == "erased@example.com" && e.Type == "verification");
        var token = System.Web.HttpUtility.ParseQueryString(new System.Uri(mail.CallbackUrl).Query)["token"];

        // The erasure lands inside the provisioning round-trip, which is the window the rebase was written
        // for — the handler is holding an instance it read before this happened.
        factory.Provisioning.DuringReprovision = async _ =>
            await factory.UserStore.DeleteAsync(federated.Id);

        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token!)]));

        Assert.Null(await factory.UserStore.GetAsync(federated.Id));
        Assert.False(await factory.UserStore.ExistsAsync(federated.Id));
        // And the caller is told the link is no longer valid rather than being shown a success.
        Assert.NotEqual(HttpStatusCode.OK, confirm.StatusCode);
    }

    /// <summary>
    /// The verification link is bound to the credential that was staged when it was ISSUED. Without that
    /// binding the link asserted only "this address is verified", so it promoted whatever happened to be
    /// staged at click time — meaning a second claimant who staged after the first link was sent had THEIR
    /// password promoted by the first claimant's click. Both emails land in the same (real owner's) inbox,
    /// so the owner clicking their own link handed the account to the later claimant.
    /// </summary>
    [Fact]
    public async Task PasswordlessClaim_LinkIsBoundToTheCredentialItWasIssuedFor()
    {
        await using var factory = new AuthagonalTestFactory { ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "race@example.com",
            NormalizedEmail = "RACE@EXAMPLE.COM",
            PasswordHash = null,
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        // Claimant ONE stages, and its link is emailed.
        await client.PostAsJsonAsync("/api/auth/register",
            new { email = "race@example.com", password = "First1234!" });
        var firstMail = factory.EmailService.SentEmails
            .Last(e => e.Email == "race@example.com" && e.Type == "verification");
        var firstToken = System.Web.HttpUtility.ParseQueryString(new System.Uri(firstMail.CallbackUrl).Query)["token"]!;

        // Claimant TWO stages afterwards, replacing the staged credential.
        await client.PostAsJsonAsync("/api/auth/register",
            new { email = "race@example.com", password = "Second999!" });

        // The owner clicks the FIRST link. It must be refused, not silently promote claimant two's password.
        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", firstToken)]));

        var afterStale = await factory.UserStore.GetAsync(federated.Id);
        Assert.Null(afterStale!.PasswordHash);

        // Neither password logs in off the back of that click.
        foreach (var pw in new[] { "First1234!", "Second999!" })
        {
            var attempt = await client.PostAsJsonAsync("/api/auth/login",
                new { email = "race@example.com", password = pw });
            Assert.NotEqual(HttpStatusCode.OK, attempt.StatusCode);
        }

        // Claimant two's OWN link still works — the binding refuses stale links, it does not brick the flow.
        var secondMail = factory.EmailService.SentEmails
            .Last(e => e.Email == "race@example.com" && e.Type == "verification");
        var secondToken = System.Web.HttpUtility.ParseQueryString(new System.Uri(secondMail.CallbackUrl).Query)["token"]!;
        Assert.NotEqual(firstToken, secondToken);

        var confirmSecond = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", secondToken)]));
        Assert.Equal(HttpStatusCode.Redirect, confirmSecond.StatusCode);

        var final = await factory.UserStore.GetAsync(federated.Id);
        Assert.NotNull(final!.PasswordHash);
        var ok = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "race@example.com", password = "Second999!" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        _ = confirm;
    }

    [Fact]
    public async Task Register_PasswordlessAccountClaim_StagesProfileAndWhitelistsAttributes()
    {
        // N1: a claim's profile/attributes must NOT touch the victim account until the fresh verification
        // click, and custom-attribute keys are whitelisted — so merely knowing a federated email can't
        // rename the account or inject attributes that would ride the real owner's tokens.
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o =>
            {
                o.AllowPasswordlessAccountClaim = true;
                o.ClaimAllowedAttributeKeys = ["org_name"]; // "evil_key" is NOT allowed
            }
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new Authagonal.Core.Models.AuthUser
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Email = "claim-attrs@example.com",
            NormalizedEmail = "CLAIM-ATTRS@EXAMPLE.COM",
            PasswordHash = null,
            FirstName = "Original",
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = System.DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "claim-attrs@example.com",
            password = "Claim1234!",
            firstName = "Attacker",
            customAttributes = new Dictionary<string, string> { ["org_name"] = "Acme", ["evil_key"] = "x" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Before the click: profile/attributes are STAGED, not applied — the victim account is untouched.
        var staged = await factory.UserStore.GetAsync(federated.Id);
        Assert.Equal("Original", staged!.FirstName);
        Assert.False(staged.CustomAttributes.ContainsKey("org_name"));
        Assert.False(staged.CustomAttributes.ContainsKey("evil_key"));

        // The click applies the staged claim — the whitelisted key lands, the non-whitelisted key is gone.
        var mail = factory.EmailService.SentEmails.Last(e => e.Email == "claim-attrs@example.com" && e.Type == "verification");
        var token = System.Web.HttpUtility.ParseQueryString(new System.Uri(mail.CallbackUrl).Query)["token"];
        // GET only renders the page now; the confirmation happens on the form post.
        var page = await client.GetAsync($"/api/auth/confirm-email?token={System.Uri.EscapeDataString(token!)}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token!)]));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);

        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.Equal("Attacker", after!.FirstName);
        Assert.Equal("Acme", after.CustomAttributes.GetValueOrDefault("org_name"));
        Assert.False(after.CustomAttributes.ContainsKey("evil_key"));
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
