using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        group.MapPost("/confirm-email", ConfirmEmailAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().DisableAntiforgery();
        group.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().DisableAntiforgery();
        group.MapGet("/session", GetSessionAsync).RequireAuthorization();
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
        IEnumerable<IAuthHook> authHooks,
        IOptions<AuthOptions> authOptions,
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

        var user = await userStore.FindByEmailAsync(request.Email, ct);
        if (user is null)
            return JsonResults.Error("invalid_credentials", 401);

        // Check if account is active
        if (!user.IsActive)
            return JsonResults.Error("account_disabled", 403);

        // Check lockout
        if (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            var remaining = user.LockoutEnd.Value - DateTimeOffset.UtcNow;
            return TypedResults.Json(new LockedOutError { Error = "locked_out", RetryAfter = (int)remaining.TotalSeconds }, AuthagonalJsonContext.Default.LockedOutError, statusCode: 423);
        }

        // Check email confirmed
        if (!user.EmailConfirmed)
            return JsonResults.Error("email_not_confirmed", 403);

        // Verify password
        var verifyResult = passwordHasher.VerifyPassword(request.Password, user.PasswordHash!);
        if (verifyResult == PasswordVerifyResult.Failed)
        {
            user.AccessFailedCount++;

            var opts = authOptions.Value;
            if (user.LockoutEnabled && user.AccessFailedCount >= opts.MaxFailedAttempts)
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(opts.LockoutDurationMinutes);
                user.AccessFailedCount = 0;
                logger.LogWarning("Account locked out for user {UserId} ({Email})", user.Id, user.Email);
            }

            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);

            await authHooks.RunOnLoginFailedAsync(request.Email!, "invalid_password", ct);

            return JsonResults.Error("invalid_credentials", 401);
        }

        // Successful login - reset lockout counters, record login time
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Rehash if needed (BCrypt -> PBKDF2 migration)
        if (verifyResult == PasswordVerifyResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(request.Password);
            logger.LogInformation("Password rehashed for user {UserId}", user.Id);
        }

        await userStore.UpdateAsync(user, ct);

        // --- MFA check ---
        // Resolve client from returnUrl (OAuth authorize context carries client_id)
        var returnUrl = httpContext.Request.Query["returnUrl"].FirstOrDefault() ?? "";
        var clientId = ExtractClientIdFromReturnUrl(returnUrl);
        OAuthClient? client = null;
        if (!string.IsNullOrEmpty(clientId))
            client = await clientStore.GetAsync(clientId, ct);

        var clientPolicy = client?.MfaPolicy ?? MfaPolicy.Disabled;
        var effectivePolicy = await authHooks.RunResolveMfaPolicyAsync(user.Id, user.Email, clientPolicy, clientId ?? "", ct);

        if (effectivePolicy != MfaPolicy.Disabled)
        {
            if (effectivePolicy == MfaPolicy.Required && !user.MfaEnabled)
            {
                // User must set up MFA — issue a setup token (reuses MfaChallenge with longer TTL)
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

            if (user.MfaEnabled)
            {
                // Create MFA challenge
                var credentials = await mfaStore.GetCredentialsAsync(user.Id, ct);
                var methods = credentials
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

                // Generate WebAuthn assertion options if the user has passkeys
                object? webAuthnOptions = null;
                var webAuthnCreds = credentials.Where(c => c.Type == MfaCredentialType.WebAuthn).ToList();
                if (webAuthnCreds.Count > 0)
                {
                    var assertionOptions = webAuthnService.CreateAssertionOptions(webAuthnCreds);
                    challenge.WebAuthnChallenge = assertionOptions.ToJson();
                    webAuthnOptions = assertionOptions;
                }

                await mfaStore.StoreChallengeAsync(challenge, ct);

                logger.LogInformation("MFA challenge created for user {UserId}", user.Id);

                return TypedResults.Json(new MfaRequiredResponse
                {
                    ChallengeId = challenge.ChallengeId,
                    Methods = methods,
                    WebAuthn = webAuthnOptions,
                }, AuthagonalJsonContext.Default.MfaRequiredResponse);
            }
        }

        // No MFA — sign cookie directly
        await CookieSignInHelper.SignInAsync(httpContext, user);

        var name = CookieSignInHelper.GetDisplayName(user);
        logger.LogInformation("User {UserId} ({Email}) signed in", user.Id, user.Email);

        await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", ct: ct);

        // If Enabled but user hasn't enrolled, hint that MFA is available
        var mfaAvailable = effectivePolicy == MfaPolicy.Enabled && !user.MfaEnabled;

        return TypedResults.Json(new LoginSuccessResponse { UserId = user.Id, Email = user.Email, Name = name, MfaAvailable = mfaAvailable, ClientId = mfaAvailable ? clientId : null }, AuthagonalJsonContext.Default.LoginSuccessResponse);
    }

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
        if (existing is not null)
            return JsonResults.Error("email_already_registered", 409);
        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = passwordHasher.HashPassword(request.Password),
            FirstName = request.FirstName?.Trim(),
            LastName = request.LastName?.Trim(),
            EmailConfirmed = email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase),
            LockoutEnabled = true,
            SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userStore.CreateAsync(user, ct);

        // Provision to downstream apps (TCC)
        try
        {
            await provisioning.ProvisionAsync(user, ct);
        }
        catch (ProvisioningException ex)
        {
            await userStore.DeleteAsync(user.Id, ct);
            logger.LogWarning(ex, "Provisioning rejected registration for {Email}", user.Email);
            return TypedResults.Json(new ErrorInfoResponse { Error = "provisioning_rejected", Message = ex.Message }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 422);
        }

        // Send verification email
        var expiresAt = DateTimeOffset.UtcNow.AddHours(ao.EmailVerificationExpiryHours).ToUnixTimeSeconds();
        var payload = $"{user.SecurityStamp}||{user.Email}||{expiresAt}";
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

        user.EmailConfirmed = true;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        logger.LogInformation("Email confirmed for user {UserId} ({Email})", user.Id, user.Email);

        return TypedResults.Json(new SuccessMessageResponse { Message = "Email confirmed successfully." }, AuthagonalJsonContext.Default.SuccessMessageResponse);
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, CancellationToken ct)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IUserStore userStore,
        IGrantStore grantStore,
        IEmailService emailService,
        ITenantContext tenantContext,
        IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
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

        // Generate a separate single-use reset token (not the security stamp)
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var resetToken = Base64UrlEncode(tokenBytes);

        // Store the token hash as a persisted grant (single-use, short expiry)
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resetToken))).ToLowerInvariant();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.Value.PasswordResetExpiryMinutes);

        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = tokenHash,
            Type = "password_reset",
            SubjectId = user.Id,
            ClientId = "auth",
            Data = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        }, ct);

        var issuer = tenantContext.Issuer;
        var callbackUrl = $"{issuer}/reset-password?p={Uri.EscapeDataString(resetToken)}";

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
        IUserStore userStore,
        IGrantStore grantStore,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        IStringLocalizer<SharedMessages> localizer,
        ILogger<Program> logger,
        CancellationToken ct)
    {
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

        var userId = grant.Data;
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
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Invalidate all refresh tokens for this user
        await grantStore.RemoveAllBySubjectAsync(user.Id, ct);

        logger.LogInformation("Password reset completed for user {UserId} ({Email})", user.Id, user.Email);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static IResult GetSessionAsync(HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var name = httpContext.User.FindFirstValue(ClaimTypes.Name);

        return TypedResults.Json(new SessionResponse { UserId = userId, Email = email, Name = name }, AuthagonalJsonContext.Default.SessionResponse);
    }

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
        CancellationToken ct)
    {
        var providers = await oidcStore.GetAllAsync(ct);
        var result = providers.Select(p => new SsoProviderInfo
        {
            ConnectionId = p.ConnectionId,
            Name = p.ConnectionName,
            LoginUrl = $"/oidc/{p.ConnectionId}/login"
        });
        return TypedResults.Json(new SsoProviderListResponse { Providers = result }, AuthagonalJsonContext.Default.SsoProviderListResponse);
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

}

public sealed class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

public sealed class RegisterRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

public sealed class ConfirmEmailRequest
{
    public string? Token { get; set; }
}

public sealed class ForgotPasswordRequest
{
    public string? Email { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string? Token { get; set; }
    public string? NewPassword { get; set; }
}
