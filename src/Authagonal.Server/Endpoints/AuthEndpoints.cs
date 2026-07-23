using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Endpoints;

public static class AuthEndpoints
{

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/register", RegisterAsync).AllowAnonymous().DisableAntiforgery();
        // GET: the clickable email-verification link (token in the query string). POST: the
        // custom-login-UI / programmatic path (token in a JSON body). The handler accepts either.
        group.MapGet("/confirm-email", ConfirmEmailAsync).AllowAnonymous();
        group.MapPost("/confirm-email", ConfirmEmailAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().DisableAntiforgery();
        group.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().DisableAntiforgery();
        group.MapGet("/session", GetSessionAsync).RequireAuthorization();
        group.MapGet("/apps", GetAppsAsync).RequireAuthorization();
        group.MapGet("/profile", GetProfileAsync).RequireAuthorization();
        group.MapPatch("/profile", UpdateProfileAsync).RequireAuthorization().DisableAntiforgery();
        group.MapGet("/sessions", GetSessionsAsync).RequireAuthorization();
        group.MapDelete("/sessions/{sessionId}", RevokeSessionAsync).RequireAuthorization().DisableAntiforgery();
        group.MapPost("/sessions/revoke-others", RevokeOtherSessionsAsync).RequireAuthorization().DisableAntiforgery();
        group.MapGet("/sso-check", SsoCheckAsync).AllowAnonymous();
        group.MapGet("/providers", GetProvidersAsync).AllowAnonymous();
        group.MapGet("/password-policy", GetPasswordPolicy).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        ISsoDomainStore ssoDomainStore,
        IClientStore clientStore,
        IMfaStore mfaStore,
        PasswordHasher passwordHasher,
        WebAuthnService webAuthnService,
        TurnstileVerifier turnstile,
        IEnumerable<IAuthHook> authHooks,
        IOptions<AuthOptions> authOptions,
        ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return JsonResults.Error("email_required");

        if (string.IsNullOrWhiteSpace(request.Password))
            return JsonResults.Error("password_required");

        // Check SSO domain first
        var domain = request.Email.Split('@', 2).LastOrDefault()?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var ssoDomain = await ssoDomainStore.GetAsync(domain, ct);
            if (ssoDomain is not null)
            {
                var ssoRedirectUrl = ssoDomain.ProviderType.Equals("oidc", StringComparison.OrdinalIgnoreCase)
                    ? $"/oidc/{ssoDomain.ConnectionId}/login"
                    : $"/saml/{ssoDomain.ConnectionId}/login";

                return TypedResults.Json(new SsoRedirectError { Error = "sso_required", RedirectUrl = ssoRedirectUrl }, AuthagonalJsonContext.Default.SsoRedirectError, statusCode: 409);
            }
        }

        // Cloudflare Turnstile (opt-in): gate the password path before the user lookup so a
        // failed challenge can't be used to probe whether an account exists.
        if (turnstile.Enabled && !await turnstile.VerifyAsync(request.TurnstileToken, httpContext.Connection.RemoteIpAddress?.ToString(), ct))
            return JsonResults.Error("captcha_failed", 400);

        var user = await userStore.FindByEmailAsync(request.Email, ct);

        // Lockout is the ONLY account state checked before the password hash — it's the brute-force
        // backstop and must short-circuit the expensive verify. Every other branch (no such user,
        // disabled, unconfirmed, wrong password) is deferred until AFTER password verification and
        // returns an identical invalid_credentials, so a wrong-password attempt can't enumerate which
        // emails exist or what state they're in.
        if (user is not null && user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            var remaining = user.LockoutEnd.Value - DateTimeOffset.UtcNow;
            return TypedResults.Json(new LockedOutError { Error = "locked_out", RetryAfter = (int)remaining.TotalSeconds }, AuthagonalJsonContext.Default.LockedOutError, statusCode: 423);
        }

        // Verify the password. For a non-existent user, verify against a fixed dummy hash so the
        // response timing matches a real account (no user-enumeration via the bcrypt/PBKDF2 cost).
        // Passwordless accounts (federated/JIT-provisioned — no local credential) verify against
        // the dummy hash too: uniform invalid_credentials instead of a 500, and no enumeration of
        // which accounts are federated.
        var verifyResult = user is { PasswordHash: not null and not "" }
            ? passwordHasher.VerifyPassword(request.Password, user.PasswordHash)
            : passwordHasher.VerifyPassword(request.Password, DummyPasswordHash(passwordHasher));

        if (user is not null && string.IsNullOrEmpty(user.PasswordHash))
            verifyResult = PasswordVerifyResult.Failed;

        if (user is null || verifyResult == PasswordVerifyResult.Failed)
        {
            if (user is not null)
            {
                // Atomic (optimistic-concurrency) increment so parallel wrong-password attempts can't
                // race the counter and slip past the lockout threshold.
                var opts = authOptions.Value;
                var locked = await userStore.RecordFailedLoginAsync(user.Id, opts.MaxFailedAttempts, TimeSpan.FromMinutes(opts.LockoutDurationMinutes), ct);
                if (locked)
                    logger.LogWarning("Account locked out for user {UserId} ({Email})", user.Id, user.Email);
            }

            // The audit-hook reason stays granular (internal only — never reaches the caller); the
            // HTTP response is a uniform invalid_credentials so the client can't distinguish them.
            await authHooks.RunOnLoginFailedAsync(request.Email!, user is null ? "user_not_found" : "invalid_password", ct);

            return JsonResults.Error("invalid_credentials", 401);
        }

        // Password verified — the caller has proven ownership, so surfacing specific account state
        // here is not enumeration (a wrong password never reaches this point).
        if (!user.IsActive)
            return JsonResults.Error("account_disabled", 403);

        // Control-plane (admin) tenants relax this so a freshly provisioned owner can sign in and
        // verify from inside the portal; every other tenant still blocks unconfirmed logins.
        if (tenantContext.RequireConfirmedEmailForLogin && !user.EmailConfirmed)
            return JsonResults.Error("email_not_confirmed", 403);

        // Successful login - reset lockout counters, record login time. Mutate the in-memory user for the
        // rest of the request (MFA etc.), but PERSIST via RecordSuccessfulLoginAsync, not UpdateAsync: the
        // latter re-encrypts every PII field just to stamp a timestamp (an encrypting store makes that ~14
        // Vault round-trips). RecordSuccessfulLoginAsync writes only the plaintext auth columns.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Rehash if needed (BCrypt -> PBKDF2 migration)
        string? rehashedPassword = null;
        if (verifyResult == PasswordVerifyResult.SuccessRehashNeeded)
        {
            rehashedPassword = passwordHasher.HashPassword(request.Password);
            user.PasswordHash = rehashedPassword;
            logger.LogInformation("Password rehashed for user {UserId}", user.Id);
        }

        await userStore.RecordSuccessfulLoginAsync(user.Id, rehashedPassword, ct);

        // --- MFA check ---
        // Resolve client from returnUrl (OAuth authorize context carries client_id)
        var returnUrl = httpContext.Request.Query["returnUrl"].FirstOrDefault() ?? "";
        var clientId = ExtractClientIdFromReturnUrl(returnUrl);
        OAuthClient? client = null;
        if (!string.IsNullOrEmpty(clientId))
            client = await clientStore.GetAsync(clientId, ct);

        var clientPolicy = client?.MfaPolicy ?? MfaPolicy.Disabled;
        var effectivePolicy = await authHooks.RunResolveMfaPolicyAsync(user.Id, user.Email, clientPolicy, clientId ?? "", ct);

        // An MFA-enrolled user is ALWAYS challenged, regardless of the client resolved from the
        // (attacker-controllable) returnUrl or the client's MfaPolicy. MFA is a property of the
        // user/session, not of the requesting client — this closes the returnUrl-driven MFA bypass.
        if (user.MfaEnabled)
        {
            // Create MFA challenge
            var credentials = await mfaStore.GetCredentialsAsync(user.Id, ct);
            // Exclude half-finished enrolments: a passkey setup that errored before confirm leaves a
            // "WebAuthn (pending)" credential (same for "TOTP (pending)"). They must never count as usable
            // MFA methods — otherwise login tries to build a passkey challenge for a credential that isn't
            // real and can lock the account out.
            var confirmedCreds = credentials
                .Where(c => c.Name is not "TOTP (pending)" and not "WebAuthn (pending)")
                .ToList();
            var methods = confirmedCreds
                .Where(c => !c.IsConsumed)
                .Select(c => c.Type)
                .Distinct()
                .Select(t => t.ToString().ToLowerInvariant())
                .ToList();

            var challenge = new MfaChallenge
            {
                ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                UserId = user.Id,
                ClientId = clientId,
                ReturnUrl = returnUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.Value.MfaChallengeExpiryMinutes),
            };

            // Generate WebAuthn assertion options if the user has (confirmed) passkeys. Wrapped in a
            // fallback: a passkey-options failure must NEVER block login — the user can still use another
            // factor (e.g. TOTP). On any error we log it and continue without the passkey option.
            string? webAuthnJson = null;
            var webAuthnCreds = confirmedCreds.Where(c => c.Type == MfaCredentialType.WebAuthn).ToList();
            if (webAuthnCreds.Count > 0)
            {
                try
                {
                    var assertionOptions = webAuthnService.CreateAssertionOptions(webAuthnCreds);
                    challenge.WebAuthnChallenge = assertionOptions.ToJson();
                    webAuthnJson = challenge.WebAuthnChallenge;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to build WebAuthn assertion options for user {UserId}; continuing without the passkey option", user.Id);
                    webAuthnJson = null;
                }
            }

            await mfaStore.StoreChallengeAsync(challenge, ct);

            logger.LogInformation("MFA challenge created for user {UserId}", user.Id);

            // AssertionOptions isn't in the source-gen JSON context (and Fido2 owns the WebAuthn wire
            // format), so build the response as raw JSON with the options embedded via ToJson() rather
            // than letting the typed serializer choke on the object-typed WebAuthn member.
            var mfaBody = new JsonObject
            {
                ["mfaRequired"] = true,
                ["challengeId"] = challenge.ChallengeId,
                ["methods"] = new JsonArray(methods.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
                ["webAuthn"] = webAuthnJson is null ? null : JsonNode.Parse(webAuthnJson),
            };
            return Results.Content(mfaBody.ToJsonString(), "application/json");
        }

        // Not enrolled, but policy requires MFA → force enrollment before any session is issued.
        if (effectivePolicy == MfaPolicy.Required)
        {
            // Issue a setup token (reuses MfaChallenge with longer TTL)
            var setupChallenge = new MfaChallenge
            {
                ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                UserId = user.Id,
                ClientId = clientId,
                ReturnUrl = returnUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.Value.MfaSetupTokenExpiryMinutes),
            };
            await mfaStore.StoreChallengeAsync(setupChallenge, ct);

            return TypedResults.Json(new MfaSetupRequiredResponse { SetupToken = setupChallenge.ChallengeId }, AuthagonalJsonContext.Default.MfaSetupRequiredResponse);
        }

        // Run the onUserAuthenticated hook BEFORE establishing the session. An enforced hook that
        // rejects the login (throws) must prevent the cookie from ever being issued — previously the
        // cookie was set first, so a rejection 500'd but still left a usable session for any client
        // that ignored the error.
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", ct: ct);

        // Not rejected — sign cookie (session carries no MFA marker).
        await CookieSignInHelper.SignInAsync(httpContext, user);

        var name = CookieSignInHelper.GetDisplayName(user);
        logger.LogInformation("User {UserId} ({Email}) signed in", user.Id, user.Email);

        // If Enabled but user hasn't enrolled, hint that MFA is available (user is not enrolled here)
        var mfaAvailable = effectivePolicy == MfaPolicy.Enabled;

        return TypedResults.Json(new LoginSuccessResponse { UserId = user.Id, Email = user.Email, Name = name, MfaAvailable = mfaAvailable, ClientId = mfaAvailable ? clientId : null }, AuthagonalJsonContext.Default.LoginSuccessResponse);
    }

    // A process-wide dummy password hash, used to spend the same hashing cost on the no-such-user
    // path as on a real verification so login timing can't distinguish whether an email exists.
    // Computed once (lazily) in the configured hash format; no real password ever matches it.
    private static string? _dummyPasswordHash;
    private static string DummyPasswordHash(PasswordHasher hasher) =>
        _dummyPasswordHash ??= hasher.HashPassword("\0unmatchable-enumeration-guard-dummy\0");

    internal static string? ExtractClientIdFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return null;

        try
        {
            // returnUrl is typically a relative path like /connect/authorize?client_id=foo&...
            var uri = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            string? query;

            if (uri.IsAbsoluteUri)
            {
                query = uri.Query;
            }
            else
            {
                // Parse as relative URI
                var qIndex = returnUrl.IndexOf('?');
                query = qIndex >= 0 ? returnUrl[qIndex..] : null;
            }

            if (query is null)
                return null;

            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
            if (parsed.TryGetValue("client_id", out var clientIdValues))
                return clientIdValues.FirstOrDefault();

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IEmailService emailService,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        IProvisioningOrchestrator provisioning,
        TurnstileVerifier turnstile,
        IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Rate limit by IP (distributed via gossip-based CRDT)
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ao = authOptions.Value;
        var rateLimited = await rateLimiter.IsRateLimitedAsync($"register|{ip}", ao.MaxRegistrationsPerIp, TimeSpan.FromMinutes(ao.RegistrationWindowMinutes), ct);
        if (rateLimited)
            return JsonResults.Error("rate_limited", "Too many registration attempts. Please try again later.", 429);

        // Cloudflare Turnstile (opt-in) — gate registration when configured.
        if (turnstile.Enabled && !await turnstile.VerifyAsync(request.TurnstileToken, ip, ct))
            return JsonResults.Error("captcha_failed", 400);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return JsonResults.Error("email_and_password_required");

        // Basic email format validation
        var emailTrimmed = request.Email.Trim();
        if (!emailTrimmed.Contains('@') || emailTrimmed.Length < 5 ||
            emailTrimmed.StartsWith('@') || emailTrimmed.EndsWith('@') || emailTrimmed.EndsWith('.'))
            return JsonResults.Error("invalid_email", "Please enter a valid email address.");

        var (isValid, validationError) = passwordValidator.Validate(request.Password, passwordPolicy);
        if (!isValid)
            return JsonResults.Error("weak_password", validationError!);

        var email = emailTrimmed.ToLowerInvariant();

        var existing = await userStore.FindByEmailAsync(email, ct);

        // Opt-in (AllowPasswordlessAccountClaim, off by default): an existing account WITH NO LOCAL
        // CREDENTIAL (a federated / JIT account) can claim a password through registration — the
        // password is set and provisioning runs, letting a downstream app act on it (e.g. bullclip's
        // guest → standard-user conversion). An account that ALREADY has a password is untouched — a
        // re-register can never overwrite a real credential. A fresh account and a claim share the
        // provisioning + verification tail.
        var isUpgrade = ao.AllowPasswordlessAccountClaim && existing is { PasswordHash: null or "" };
        if (existing is not null && !isUpgrade)
        {
            // Don't reveal that the email is already taken (account enumeration). Notify the real
            // owner so they can sign in / reset, and return the SAME neutral 201 a brand-new
            // registration returns. Spend the password-hash cost too, so timing can't distinguish a
            // taken email from a new one. (UserId is a throwaway here; the client doesn't use it.)
            _ = passwordHasher.HashPassword(request.Password);
            try
            {
                await emailService.SendAccountExistsEmailAsync(existing.Email, $"{tenantContext.Issuer}/login", ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send account-exists email to {Email}", existing.Email);
            }
            logger.LogInformation("Registration attempt for an existing credentialed email — neutral response returned");
            return TypedResults.Json(new RegistrationSuccess { Success = true, UserId = Guid.NewGuid().ToString("D") }, AuthagonalJsonContext.Default.RegistrationSuccess, statusCode: 201);
        }

        AuthUser user;
        // ---- Side-effect boundary: from here on the registration MUST run to completion. ----
        // A browser abort (tab closed, navigation, client timeout) must not cancel half-way
        // through persistence/provisioning: honoring it here left accounts provisioned downstream
        // but unpersisted (or vice versa). Validation above still honors the caller's token.
        ct = CancellationToken.None;

        if (isUpgrade)
        {
            // Re-prove inbox ownership at CLAIM time: the account was born from an emailed link,
            // and the claim must not inherit that proof — anyone who merely KNOWS the email could
            // otherwise take the account over instantly. The credential is STAGED
            // (PendingPasswordHash) and provisioning DEFERRED; both activate only when the fresh
            // verification email below is clicked (ConfirmEmailAsync promotes + converts).
            user = existing!;
            user.PendingPasswordHash = passwordHasher.HashPassword(request.Password);
            // STAGE the claim's profile/attributes rather than applying them to the victim account now —
            // they activate only when the fresh verification email is clicked (ConfirmEmailAsync). This
            // stops anyone who merely KNOWS the email from mutating the account's name/custom-attributes
            // (which ride the real owner's tokens) pre-verification. Custom-attribute keys are whitelisted
            // (ClaimAllowedAttributeKeys; empty = allow all) so a claim can't inject arbitrary attributes.
            user.PendingClaimJson = BuildPendingClaimJson(request, ao.ClaimAllowedAttributeKeys);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }
        else
        {
            user = new AuthUser
            {
                Id = Guid.NewGuid().ToString("D"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = passwordHasher.HashPassword(request.Password),
                FirstName = request.FirstName?.Trim(),
                LastName = request.LastName?.Trim(),
                Locale = NormalizeLocale(request.Locale),
                EmailConfirmed = IsAutoConfirmedDomain(email, ao.AutoConfirmEmailDomains),
                LockoutEnabled = true,
                SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                CreatedAt = DateTimeOffset.UtcNow,
                CustomAttributes = request.CustomAttributes is { Count: > 0 }
                    ? new Dictionary<string, string>(request.CustomAttributes)
                    : [],
            };

            await userStore.CreateAsync(user, ct);
        }

        // Provision to downstream apps (TCC). Try handlers may return an
        // OrganizationId and/or CustomAttributes that the orchestrator merges
        // onto the user — persist that merge so those values land on tokens.
        // An UPGRADE forces reprovisioning: the account was already provisioned (e.g. a guest adopted
        // via a share-link federation), so a plain ProvisionAsync would skip it — the downstream would
        // never see the claim's signup context (org name) and couldn't convert the guest to a real user.
        if (!isUpgrade)
        {
            try
            {
                await provisioning.ProvisionAsync(user, ct);
            }
            catch (ProvisioningException ex)
            {
                // Roll back the brand-new account.
                await userStore.DeleteAsync(user.Id, ct);
                logger.LogWarning(ex, "Provisioning rejected registration for {Email}", user.Email);
                return TypedResults.Json(new ErrorInfoResponse { Error = "provisioning_rejected", Message = ex.Message }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 422);
            }
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Already confirmed — provisioning vouched for the address (invite redemption) or the
        // domain is auto-confirmed. No verification email: the user can sign straight in.
        // NEVER for a claim: its stored confirmation belongs to a different flow's proof — the
        // claim requires its own click, so it falls through to the verification email.
        if (user.EmailConfirmed && !isUpgrade)
        {
            logger.LogInformation("User registered (email pre-verified): {UserId} ({Email})", user.Id, user.Email);
            return TypedResults.Json(new RegistrationSuccess { Success = true, UserId = user.Id, EmailVerified = true }, AuthagonalJsonContext.Default.RegistrationSuccess, statusCode: 201);
        }

        // Send verification email. The optional 4th payload segment carries the OAuth client the
        // registration flow originated from (parsed from the login page's authorize returnUrl), so
        // the confirmation landing can offer "continue to {app}". Older 3-segment tokens stay valid.
        var flowClientId = ExtractClientIdFromReturnUrl(httpContext.Request.Query["returnUrl"].FirstOrDefault());
        var expiresAt = DateTimeOffset.UtcNow.AddHours(ao.EmailVerificationExpiryHours).ToUnixTimeSeconds();
        var payload = flowClientId is null
            ? $"{user.SecurityStamp}||{user.Email}||{expiresAt}"
            : $"{user.SecurityStamp}||{user.Email}||{expiresAt}||{flowClientId}";
        var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var issuer = tenantContext.Issuer;
        var callbackUrl = $"{issuer}/api/auth/confirm-email?token={Uri.EscapeDataString(encodedPayload)}";

        try
        {
            await emailService.SendVerificationEmailAsync(user.Email, callbackUrl, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send verification email to {Email}", user.Email);
        }

        logger.LogInformation("User registered: {UserId} ({Email})", user.Id, user.Email);

        return TypedResults.Json(new RegistrationSuccess { Success = true, UserId = user.Id }, AuthagonalJsonContext.Default.RegistrationSuccess, statusCode: 201);
    }

    private static async Task<IResult> ConfirmEmailAsync(
        HttpContext httpContext,
        IUserStore userStore,
        IClientStore clientStore,
        IEnumerable<IAuthHook> authHooks,
        IProvisioningOrchestrator provisioning,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Accept token from query string (email link click) or JSON body
        var token = httpContext.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token) && httpContext.Request.HasJsonContentType())
        {
            var body = await httpContext.Request.ReadFromJsonAsync<ConfirmEmailRequest>(ct);
            token = body?.Token;
        }

        if (string.IsNullOrWhiteSpace(token))
            return JsonResults.Error("invalid_request", "Token is required.");

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch
        {
            return JsonResults.Error("invalid_token", "Invalid token format.");
        }

        var parts = decoded.Split("||");
        if (parts.Length < 3)
            return JsonResults.Error("invalid_token", "Invalid token format.");

        var securityStamp = parts[0];
        var email = parts[1];

        if (!long.TryParse(parts[2], out var expiresAtUnix) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
        {
            return JsonResults.Error("token_expired", "This verification link has expired.");
        }

        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
            return JsonResults.Error("invalid_token", "Invalid or expired verification link.");

        if (user.SecurityStamp != securityStamp)
            return JsonResults.Error("invalid_token", "This verification link has already been used or has expired.");

        // Passwordless-account claim completion: this click IS the fresh ownership proof the claim
        // was waiting for. Run the downstream conversion FIRST (it may reject — e.g. seat policy),
        // then promote the staged credential. Side effects run to completion regardless of the
        // caller's abort token (same shielding as registration).
        if (!string.IsNullOrWhiteSpace(user.PendingPasswordHash))
        {
            // This click IS the fresh ownership proof. Apply the staged profile/attributes (in memory)
            // so the downstream conversion sees the claim's signup context (org name, etc.), then run it.
            ApplyPendingClaim(user);
            try
            {
                await provisioning.ReprovisionAsync(user, CancellationToken.None);
            }
            catch (ProvisioningException ex)
            {
                // Claim fails cleanly: reload a CLEAN copy so none of the staged profile/attributes
                // applied above persist, then drop the staged credential + claim. The account stays
                // passwordless (federation login intact) and remains claimable — victim data untouched.
                var clean = await userStore.FindByEmailAsync(email, CancellationToken.None) ?? user;
                clean.PendingPasswordHash = null;
                clean.PendingClaimJson = null;
                clean.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                clean.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(clean, CancellationToken.None);
                logger.LogWarning(ex, "Passwordless-claim conversion rejected for {Email}", user.Email);
                return HttpMethods.IsGet(httpContext.Request.Method)
                    ? Results.Redirect($"/login?error=provisioning_rejected&error_description={Uri.EscapeDataString(ex.Message)}")
                    : JsonResults.Error("provisioning_rejected", ex.Message);
            }
            user.PasswordHash = user.PendingPasswordHash;
            user.PendingPasswordHash = null;
            user.PendingClaimJson = null;
            logger.LogInformation("Passwordless account claimed by {UserId} ({Email}) — credential promoted after fresh verification", user.Id, user.Email);
        }

        user.EmailConfirmed = true;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, CancellationToken.None);

        logger.LogInformation("Email confirmed for user {UserId} ({Email})", user.Id, user.Email);

        // Notify hooks (e.g. the Cloud lifts the unverified-tenant user cap when the owner confirms).
        await authHooks.RunOnEmailConfirmedAsync(user.Id, user.Email, ct);

        // Optional 4th token segment = the client the registration flow came from (stamped by
        // RegisterAsync, integrity-backed by the security-stamp check above). It rides to the login
        // page so the post-sign-in destination can be that app instead of the account page.
        var flowClientId = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;

        // A clicked email link (GET) lands the user on the login page, not raw JSON; the programmatic
        // POST path keeps the JSON contract (now with the resolved continue-to-app link, if any).
        if (HttpMethods.IsGet(httpContext.Request.Method))
            return Results.Redirect(flowClientId is null
                ? "/login?email_confirmed=1"
                : $"/login?email_confirmed=1&continue_client={Uri.EscapeDataString(flowClientId)}");
        var appLink = await ResolveAppLinkAsync(clientStore, flowClientId, ct);
        return TypedResults.Json(
            new ConfirmEmailResponse { Message = "Email confirmed successfully.", AppLink = appLink },
            AuthagonalJsonContext.Default.ConfirmEmailResponse);
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, CancellationToken ct)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IGrantStore grantStore,
        IEmailService emailService,
        ITenantContext tenantContext,
        TurnstileVerifier turnstile,
        IRateLimiter rateLimiter,
        IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Cloudflare Turnstile (opt-in): reject bots before issuing reset tokens / sending mail.
        // A captcha result is independent of account existence, so this leaks no enumeration signal.
        if (turnstile.Enabled && !await turnstile.VerifyAsync(request.TurnstileToken, httpContext.Connection.RemoteIpAddress?.ToString(), ct))
            return JsonResults.Error("captcha_failed", 400);

        // Always return success to prevent email enumeration
        if (string.IsNullOrWhiteSpace(request.Email))
            return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);

        var user = await userStore.FindByEmailAsync(request.Email, ct);
        if (user is null)
        {
            logger.LogInformation("Password reset requested for non-existent email: {Email}", request.Email);
            // Artificial delay to prevent timing-based email enumeration
            await Task.Delay(TimeSpan.FromMilliseconds(100 + RandomNumberGenerator.GetInt32(200)), ct);
            return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
        }

        // Per-email rate limit so a single address can't be flooded with reset emails regardless of
        // source IP (the per-IP strict limiter alone doesn't bound emails to one victim). Stay
        // enumeration-neutral: when over the cap, skip sending but return the same success response.
        var rlOpts = authOptions.Value;
        if (await rateLimiter.IsRateLimitedAsync($"pwreset|{user.Email}", rlOpts.MaxPasswordResetsPerEmail, TimeSpan.FromMinutes(rlOpts.PasswordResetWindowMinutes), ct))
        {
            logger.LogInformation("Password reset rate-limited for {Email}", user.Email);
            return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
        }

        // Generate a separate single-use reset token (not the security stamp)
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var resetToken = Base64UrlEncode(tokenBytes);

        // Store the token hash as a persisted grant (single-use, short expiry)
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resetToken))).ToLowerInvariant();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.Value.PasswordResetExpiryMinutes);

        // Data carries the user id plus, when the forgot form was reached from an authorize flow,
        // the originating client ("userId||clientId") so the reset-complete page can offer
        // "continue to {app}". ClientId stays the "auth" marker — grant queries key on it.
        var flowClientId = ExtractClientIdFromReturnUrl(httpContext.Request.Query["returnUrl"].FirstOrDefault());
        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = tokenHash,
            Type = "password_reset",
            SubjectId = user.Id,
            ClientId = "auth",
            Data = flowClientId is null ? user.Id : $"{user.Id}||{flowClientId}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        }, ct);

        var issuer = tenantContext.Issuer;
        // The login SPA is mounted under /login (App basename), so the reset page lives at
        // /login/reset-password — the link must include that prefix or it renders a blank page.
        var callbackUrl = $"{issuer}/login/reset-password?p={Uri.EscapeDataString(resetToken)}";

        try
        {
            await emailService.SendPasswordResetEmailAsync(user.Email, callbackUrl, ct);
            logger.LogInformation("Password reset email sent to {Email}", user.Email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
        }

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IGrantStore grantStore,
        IClientStore clientStore,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        TurnstileVerifier turnstile,
        IStringLocalizer<SharedMessages> localizer,
        IEnumerable<IAuthHook> authHooks,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Cloudflare Turnstile (opt-in): gate the token-redemption path against automated abuse.
        if (turnstile.Enabled && !await turnstile.VerifyAsync(request.TurnstileToken, httpContext.Connection.RemoteIpAddress?.ToString(), ct))
            return JsonResults.Error("captcha_failed", 400);

        if (string.IsNullOrWhiteSpace(request.Token))
            return JsonResults.Error("invalid_token", localizer["Auth_ResetTokenRequired"].Value);

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return JsonResults.Error("password_required", localizer["Auth_PasswordRequired"].Value);

        // Validate password strength
        var (isValid, validationError) = passwordValidator.Validate(request.NewPassword, passwordPolicy);
        if (!isValid)
            return JsonResults.Error("weak_password", validationError!);

        // Look up the reset token grant by its hash
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))).ToLowerInvariant();
        var grant = await grantStore.GetAsync(tokenHash, ct);

        if (grant is null || grant.Type != "password_reset")
            return JsonResults.Error("invalid_token", localizer["Auth_InvalidToken"].Value);

        // Check expiration
        if (DateTimeOffset.UtcNow > grant.ExpiresAt)
        {
            await grantStore.RemoveAsync(tokenHash, ct);
            return JsonResults.Error("token_expired", localizer["Auth_TokenExpired"].Value);
        }

        // Check if already consumed (single-use)
        if (grant.ConsumedAt is not null)
        {
            await grantStore.RemoveAsync(tokenHash, ct);
            return JsonResults.Error("token_expired", localizer["Auth_TokenUsedOrExpired"].Value);
        }

        // Data is "userId" or "userId||clientId" (the flow's originating client, stamped by
        // ForgotPasswordAsync) — see the grant write for the format rationale.
        var dataParts = grant.Data.Split("||", 2);
        var userId = dataParts[0];
        var flowClientId = dataParts.Length > 1 && !string.IsNullOrWhiteSpace(dataParts[1]) ? dataParts[1] : null;
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
        {
            await grantStore.RemoveAsync(tokenHash, ct);
            return JsonResults.Error("invalid_token", localizer["Auth_InvalidToken"].Value);
        }

        // Delete the grant immediately (single-use)
        await grantStore.RemoveAsync(tokenHash, ct);

        // Reset password and rotate security stamp (invalidates all existing sessions)
        user.PasswordHash = passwordHasher.HashPassword(request.NewPassword);
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        // Completing a reset proves control of the email, so it also confirms an unverified account:
        // the reset link IS a proof-of-control challenge, and we shouldn't dead-end a user who never
        // verified and then forgot their password.
        var newlyConfirmed = !user.EmailConfirmed;
        user.EmailConfirmed = true;
        // Refresh the stored locale from the reset page's UI language (optional, no extra write).
        var resetLocale = NormalizeLocale(request.Locale);
        if (resetLocale is not null) user.Locale = resetLocale;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Invalidate all refresh tokens for this user
        await grantStore.RemoveAllBySubjectAsync(user.Id, ct);

        await authHooks.RunOnPasswordChangedAsync(user.Id, user.Email, "reset", ct);

        // Lift the unverified-tenant cap etc. if this reset is what first confirmed the email.
        if (newlyConfirmed)
            await authHooks.RunOnEmailConfirmedAsync(user.Id, user.Email, ct);

        logger.LogInformation("Password reset completed for user {UserId} ({Email})", user.Id, user.Email);

        // Offer the reset-complete page a "continue to {app}" target: the flow's client, else the
        // tenant default. Null keeps the plain "sign in" UX.
        var appLink = await ResolveAppLinkAsync(clientStore, flowClientId, ct);
        return TypedResults.Json(
            new ResetPasswordResponse { Success = true, AppLink = appLink },
            AuthagonalJsonContext.Default.ResetPasswordResponse);
    }

    private static IResult GetSessionAsync(HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var name = httpContext.User.FindFirstValue(ClaimTypes.Name);

        return TypedResults.Json(new SessionResponse { UserId = userId, Email = email, Name = name }, AuthagonalJsonContext.Default.SessionResponse);
    }

    // Application links for the account pages' "back to app" button / launcher: enabled clients
    // with a home URI (initiate-login preferred over client URI — the RP then originates a real
    // authorize flow). Default resolution: the explicitly flagged client wins; when none is
    // flagged and exactly one client has a home URI, it is the implicit default. Home URIs are
    // operator-entered config validated at write time (see Admin.ClientEndpoints), never derived
    // from request input, so they are safe to hand to the SPA as navigation targets.
    private static async Task<IResult> GetAppsAsync(IClientStore clientStore, CancellationToken ct)
    {
        var clients = await clientStore.GetAllAsync(ct);
        var apps = clients
            .Where(c => c.Enabled && (!string.IsNullOrWhiteSpace(c.InitiateLoginUri) || !string.IsNullOrWhiteSpace(c.ClientUri)))
            .Select(c => new AppLinkResponse
            {
                ClientId = c.ClientId,
                ClientName = string.IsNullOrWhiteSpace(c.ClientName) ? c.ClientId : c.ClientName,
                HomeUri = !string.IsNullOrWhiteSpace(c.InitiateLoginUri) ? c.InitiateLoginUri! : c.ClientUri!,
                LogoUri = c.LogoUri,
                IsDefault = c.IsDefaultApplication,
            })
            .OrderBy(a => a.ClientName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The API presents exactly one default: single-app tenants get it implicitly, and a
        // store-level race that left two flags set is tie-broken rather than surfaced.
        var defaults = apps.Where(a => a.IsDefault).ToList();
        if (defaults.Count == 0 && apps.Count == 1)
            apps[0].IsDefault = true;
        else if (defaults.Count > 1)
            foreach (var a in defaults.Skip(1)) a.IsDefault = false;

        return TypedResults.Json(apps, AuthagonalJsonContext.Default.ListAppLinkResponse);
    }

    /// <summary>
    /// The single "continue to app" target for an email-flow completion page: the flow's
    /// originating client when it has a home URI, else the tenant's default application
    /// (explicit flag, else the only client with a home URI), else null (caller keeps the
    /// plain "sign in" UX). Anonymous-safe: exposes only operator-entered client name and
    /// home URI — the same information the sign-in page's branding already shows.
    /// </summary>
    internal static async Task<AppLinkResponse?> ResolveAppLinkAsync(
        IClientStore clientStore, string? preferredClientId, CancellationToken ct)
    {
        static string? HomeUri(OAuthClient c) =>
            !string.IsNullOrWhiteSpace(c.InitiateLoginUri) ? c.InitiateLoginUri
            : !string.IsNullOrWhiteSpace(c.ClientUri) ? c.ClientUri
            : null;

        var withHome = (await clientStore.GetAllAsync(ct))
            .Where(c => c.Enabled && HomeUri(c) is not null)
            .ToList();

        var pick =
            (preferredClientId is not null ? withHome.FirstOrDefault(c => c.ClientId == preferredClientId) : null)
            ?? withHome.FirstOrDefault(c => c.IsDefaultApplication)
            ?? (withHome.Count == 1 ? withHome[0] : null);

        return pick is null ? null : new AppLinkResponse
        {
            ClientId = pick.ClientId,
            ClientName = string.IsNullOrWhiteSpace(pick.ClientName) ? pick.ClientId : pick.ClientName,
            HomeUri = HomeUri(pick)!,
            LogoUri = pick.LogoUri,
            IsDefault = pick.IsDefaultApplication,
        };
    }

    // Self-service profile: the authenticated user reads and updates their own non-sensitive profile
    // fields (incl. locale). Email, password, roles, active state and org are NOT editable here.
    private static async Task<IResult> GetProfileAsync(HttpContext httpContext, IUserStore userStore, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
        var user = userId is null ? null : await userStore.GetAsync(userId, ct);
        if (user is null)
            return JsonResults.Error("user_not_found", 404);

        return TypedResults.Json(ProfileOf(user), AuthagonalJsonContext.Default.ProfileResponse);
    }

    private static async Task<IResult> UpdateProfileAsync(ProfileUpdateRequest request, HttpContext httpContext, IUserStore userStore, IEnumerable<IAuthHook> authHooks, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
        var user = userId is null ? null : await userStore.GetAsync(userId, ct);
        if (user is null)
            return JsonResults.Error("user_not_found", 404);

        if (request.FirstName is not null) user.FirstName = request.FirstName.Trim();
        if (request.LastName is not null) user.LastName = request.LastName.Trim();
        if (request.CompanyName is not null) user.CompanyName = request.CompanyName.Trim();
        if (request.Phone is not null) user.Phone = request.Phone.Trim();
        if (request.Locale is not null)
        {
            var loc = NormalizeLocale(request.Locale);
            if (loc is not null) user.Locale = loc;
        }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);
        await authHooks.RunOnUserUpdatedAsync(user.Id, user.Email, "self", ct);

        return TypedResults.Json(ProfileOf(user), AuthagonalJsonContext.Default.ProfileResponse);
    }

    private static async Task<IResult> GetSessionsAsync(HttpContext httpContext, IUserSessionRegistry? registry, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
        if (userId is null) return JsonResults.Error("user_not_found", 404);
        if (registry is null) return TypedResults.Json(new ActiveSessionsResponse(), AuthagonalJsonContext.Default.ActiveSessionsResponse);
        var sessions = await registry.ListAsync(userId, CurrentSessionId(httpContext), ct);
        var view = new ActiveSessionsResponse { Sessions = sessions.Select(SessionViewOf).ToList() };
        return TypedResults.Json(view, AuthagonalJsonContext.Default.ActiveSessionsResponse);
    }

    private static async Task<IResult> RevokeSessionAsync(string sessionId, HttpContext httpContext, IUserSessionRegistry? registry, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
        if (userId is null) return JsonResults.Error("user_not_found", 404);
        if (registry is null) return JsonResults.Error("not_supported", 404);
        var ok = await registry.RevokeAsync(userId, sessionId, ct);
        return ok
            ? TypedResults.Json(new RevokeSessionsResponse { Revoked = 1 }, AuthagonalJsonContext.Default.RevokeSessionsResponse)
            : JsonResults.Error("session_not_found", 404);
    }

    private static async Task<IResult> RevokeOtherSessionsAsync(HttpContext httpContext, IUserSessionRegistry? registry, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
        if (userId is null) return JsonResults.Error("user_not_found", 404);
        if (registry is null) return TypedResults.Json(new RevokeSessionsResponse(), AuthagonalJsonContext.Default.RevokeSessionsResponse);
        var revoked = await registry.RevokeOthersAsync(userId, CurrentSessionId(httpContext), ct);
        return TypedResults.Json(new RevokeSessionsResponse { Revoked = revoked }, AuthagonalJsonContext.Default.RevokeSessionsResponse);
    }

    private static string? CurrentSessionId(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(IUserSessionRegistry.CurrentSessionItem, out var v) ? v as string : null;

    private static ActiveSessionView SessionViewOf(SessionDescriptor s) => new()
    {
        SessionId = s.SessionId,
        Current = s.Current,
        CreatedAt = s.CreatedAt,
        LastSeenAt = s.LastSeenAt,
        ExpiresAt = s.ExpiresAt,
        Ip = s.Ip,
        UserAgent = s.UserAgent,
    };

    private static ProfileResponse ProfileOf(AuthUser user) => new()
    {
        Email = user.Email,
        EmailConfirmed = user.EmailConfirmed,
        FirstName = user.FirstName,
        LastName = user.LastName,
        CompanyName = user.CompanyName,
        Phone = user.Phone,
        Locale = user.Locale,
    };

    /// <summary>Trim a client-supplied locale tag; reject empty or implausibly long values (returns null).
    /// Delegates to the shared <see cref="Services.Locales"/> so registration/reset/profile and SCIM
    /// provisioning normalize identically.</summary>
    private static string? NormalizeLocale(string? locale) => Locales.Normalize(locale);

    private static IResult GetPasswordPolicy(
        PasswordPolicy policy,
        IStringLocalizer<SharedMessages> localizer)
    {
        var rules = new List<PasswordPolicyRule>();

        rules.Add(new PasswordPolicyRule { Rule = "minLength", Value = policy.MinLength, Label = string.Format(localizer["PasswordPolicy_MinLength"].Value, policy.MinLength) });

        if (policy.RequireUppercase)
            rules.Add(new PasswordPolicyRule { Rule = "uppercase", Label = localizer["PasswordPolicy_Uppercase"].Value });

        if (policy.RequireLowercase)
            rules.Add(new PasswordPolicyRule { Rule = "lowercase", Label = localizer["PasswordPolicy_Lowercase"].Value });

        if (policy.RequireDigit)
            rules.Add(new PasswordPolicyRule { Rule = "digit", Label = localizer["PasswordPolicy_Digit"].Value });

        if (policy.RequireSpecialChar)
            rules.Add(new PasswordPolicyRule { Rule = "specialChar", Label = localizer["PasswordPolicy_SpecialChar"].Value });

        return TypedResults.Json(new PasswordPolicyResponse { Rules = rules }, AuthagonalJsonContext.Default.PasswordPolicyResponse);
    }

    private static async Task<IResult> GetProvidersAsync(
        IOidcProviderStore oidcStore,
        ISamlProviderStore samlStore,
        IOptions<TurnstileOptions> turnstileOptions,
        CancellationToken ct)
    {
        var response = await BuildProvidersResponseAsync(oidcStore, samlStore, turnstileOptions.Value.SiteKey, ct);
        return TypedResults.Json(response, AuthagonalJsonContext.Default.SsoProviderListResponse);
    }

    /// <summary>
    /// Builds the login page's provider-list payload (the <c>/api/auth/providers</c> response body).
    /// Public so a host can inline the exact same payload into the login document it serves
    /// (a <c>window.__AUTHAGONAL_BOOT__</c> script the login SPA consumes), sparing far-from-origin
    /// visitors the extra round trip that otherwise serializes first paint.
    /// </summary>
    public static async Task<SsoProviderListResponse> BuildProvidersResponseAsync(
        IOidcProviderStore oidcStore,
        ISamlProviderStore samlStore,
        string? turnstileSiteKey,
        CancellationToken ct)
    {
        // Render a "Continue with {name}" button only for connections that are NOT domain-routed and
        // not marked hidden (ShowOnLogin). A connection with AllowedDomains is reached email-first via
        // /sso-check; a ShowOnLogin=false connection is reached only via an explicit idp_hint (e.g. the
        // bullclip guest-link provider). Covers both OIDC and SAML uniformly.
        // The two reads hit independent tables — run them concurrently; this payload sits on the
        // login page's first-paint path.
        var oidcTask = oidcStore.GetAllAsync(ct);
        var samlTask = samlStore.GetAllAsync(ct);
        var oidc = await oidcTask;
        var saml = await samlTask;
        var result = oidc
            .Where(p => p.AllowedDomains.Count == 0 && p.ShowOnLogin)
            .Select(p => new SsoProviderInfo
            {
                ConnectionId = p.ConnectionId,
                Name = p.ConnectionName,
                Type = "oidc",
                IconUrl = p.IconUrl,
                LoginUrl = $"/oidc/{p.ConnectionId}/login"
            })
            .Concat(saml
                .Where(p => p.AllowedDomains.Count == 0)
                .Select(p => new SsoProviderInfo
                {
                    ConnectionId = p.ConnectionId,
                    Name = p.ConnectionName,
                    Type = "saml",
                    IconUrl = p.IconUrl,
                    LoginUrl = $"/saml/{p.ConnectionId}/login"
                }))
            .ToList();
        return new SsoProviderListResponse { Providers = result, TurnstileSiteKey = turnstileSiteKey };
    }

    private static async Task<IResult> SsoCheckAsync(
        string? email,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return JsonResults.Error("email_required");

        var domain = email.Split('@', 2).LastOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
            return TypedResults.Json(new SsoCheckResponse { SsoRequired = false }, AuthagonalJsonContext.Default.SsoCheckResponse);

        var ssoDomain = await ssoDomainStore.GetAsync(domain, ct);
        if (ssoDomain is null)
            return TypedResults.Json(new SsoCheckResponse { SsoRequired = false }, AuthagonalJsonContext.Default.SsoCheckResponse);

        var redirectUrl = ssoDomain.ProviderType.Equals("oidc", StringComparison.OrdinalIgnoreCase)
            ? $"/oidc/{ssoDomain.ConnectionId}/login"
            : $"/saml/{ssoDomain.ConnectionId}/login";

        return TypedResults.Json(new SsoCheckResponse
        {
            SsoRequired = true,
            ProviderType = ssoDomain.ProviderType,
            ConnectionId = ssoDomain.ConnectionId,
            RedirectUrl = redirectUrl
        }, AuthagonalJsonContext.Default.SsoCheckResponse);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // Auto-confirm a registration's email only when its domain is explicitly allow-listed
    // (Auth:AutoConfirmEmailDomains). Empty list (the default) means every registration must verify.
    private static bool IsAutoConfirmedDomain(string email, List<string> autoConfirmDomains)
    {
        if (autoConfirmDomains.Count == 0)
            return false;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return false;
        var domain = email[(at + 1)..];
        return autoConfirmDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
    }

    // Build the staged claim payload (JSON) from a register request, whitelisting custom-attribute keys
    // (empty allowlist = allow all). Returns null when there is nothing to stage.
    private static string? BuildPendingClaimJson(RegisterRequest request, List<string> allowedAttributeKeys)
    {
        var data = new PendingClaimData
        {
            FirstName = string.IsNullOrWhiteSpace(request.FirstName) ? null : request.FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(request.LastName) ? null : request.LastName.Trim(),
        };
        if (request.CustomAttributes is { Count: > 0 })
        {
            foreach (var kv in request.CustomAttributes)
                if (allowedAttributeKeys.Count == 0 || allowedAttributeKeys.Contains(kv.Key))
                    data.CustomAttributes[kv.Key] = kv.Value;
        }
        if (data.FirstName is null && data.LastName is null && data.CustomAttributes.Count == 0)
            return null;
        return JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.PendingClaimData);
    }

    // Apply a claim's staged profile/attributes to the user in memory. No-op if nothing is staged or the
    // blob is malformed — a corrupt stage must never block the claim's confirmation.
    private static void ApplyPendingClaim(AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(user.PendingClaimJson))
            return;
        PendingClaimData? data;
        try
        {
            data = JsonSerializer.Deserialize(user.PendingClaimJson, AuthagonalJsonContext.Default.PendingClaimData);
        }
        catch (JsonException)
        {
            return;
        }
        if (data is null)
            return;
        if (!string.IsNullOrWhiteSpace(data.FirstName)) user.FirstName = data.FirstName;
        if (!string.IsNullOrWhiteSpace(data.LastName)) user.LastName = data.LastName;
        foreach (var kv in data.CustomAttributes)
            user.CustomAttributes[kv.Key] = kv.Value;
    }

}

/// <summary>Profile/attributes STAGED by a passwordless-account claim, serialized into
/// <see cref="AuthUser.PendingClaimJson"/> and applied only when the claim's verification email is
/// clicked. Custom-attribute keys are whitelisted at stage time (see AuthOptions.ClaimAllowedAttributeKeys).</summary>
internal sealed class PendingClaimData
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public Dictionary<string, string> CustomAttributes { get; set; } = [];
}

public sealed class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    /// <summary>Cloudflare Turnstile token; verified only when Turnstile is configured.</summary>
    public string? TurnstileToken { get; set; }
}

public sealed class RegisterRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    /// <summary>Preferred UI language (BCP-47 tag) captured from the registration page; persisted on the user.</summary>
    public string? Locale { get; set; }
    public Dictionary<string, string>? CustomAttributes { get; set; }
    /// <summary>Cloudflare Turnstile token; verified only when Turnstile is configured.</summary>
    public string? TurnstileToken { get; set; }
}

public sealed class ConfirmEmailRequest
{
    public string? Token { get; set; }
}

public sealed class ForgotPasswordRequest
{
    public string? Email { get; set; }
    /// <summary>Cloudflare Turnstile token; verified only when Turnstile is configured.</summary>
    public string? TurnstileToken { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string? Token { get; set; }
    public string? NewPassword { get; set; }
    /// <summary>Preferred UI language (BCP-47 tag) from the reset page; refreshes the user's stored locale.</summary>
    public string? Locale { get; set; }
    /// <summary>Cloudflare Turnstile token; verified only when Turnstile is configured.</summary>
    public string? TurnstileToken { get; set; }
}

/// <summary>Self-service profile update — only the user's own non-sensitive fields. Null fields are unchanged.</summary>
public sealed class ProfileUpdateRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    /// <summary>Preferred UI/communication language as a BCP-47 tag.</summary>
    public string? Locale { get; set; }
}
