using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Authagonal.Server.Endpoints;

public static class AuthEndpoints
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("auth");
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting("auth");
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting("auth");
        group.MapGet("/session", GetSessionAsync).RequireAuthorization();
        group.MapGet("/sso-check", SsoCheckAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        ISsoDomainStore ssoDomainStore,
        PasswordHasher passwordHasher,
        IAuthHook authHook,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Results.Json(new { error = "email_required" }, statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.Password))
            return Results.Json(new { error = "password_required" }, statusCode: 400);

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

                return Results.Json(new { error = "sso_required", redirectUrl = ssoRedirectUrl }, statusCode: 409);
            }
        }

        var user = await userStore.FindByEmailAsync(request.Email, ct);
        if (user is null)
            return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);

        // Check lockout
        if (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            var remaining = user.LockoutEnd.Value - DateTimeOffset.UtcNow;
            return Results.Json(new { error = "locked_out", retryAfter = (int)remaining.TotalSeconds }, statusCode: 423);
        }

        // Check email confirmed
        if (!user.EmailConfirmed)
            return Results.Json(new { error = "email_not_confirmed" }, statusCode: 403);

        // Verify password
        var verifyResult = passwordHasher.VerifyPassword(request.Password, user.PasswordHash!);
        if (verifyResult == PasswordVerifyResult.Failed)
        {
            user.AccessFailedCount++;

            if (user.LockoutEnabled && user.AccessFailedCount >= MaxFailedAttempts)
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.Add(LockoutDuration);
                user.AccessFailedCount = 0;
                logger.LogWarning("Account locked out for user {UserId} ({Email})", user.Id, user.Email);
            }

            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);

            await authHook.OnLoginFailedAsync(request.Email!, "invalid_password", ct);

            return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
        }

        // Successful login - reset lockout counters
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Rehash if needed (BCrypt -> PBKDF2 migration)
        if (verifyResult == PasswordVerifyResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(request.Password);
            logger.LogInformation("Password rehashed for user {UserId}", user.Id);
        }

        await userStore.UpdateAsync(user, ct);

        // Create claims and sign in
        var name = $"{user.FirstName} {user.LastName}".Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, name),
            new("security_stamp", user.SecurityStamp ?? "")
        };

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        logger.LogInformation("User {UserId} ({Email}) signed in", user.Id, user.Email);

        await authHook.OnUserAuthenticatedAsync(user.Id, user.Email, "password", ct: ct);

        return Results.Ok(new { userId = user.Id, email = user.Email, name });
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext, CancellationToken ct)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IUserStore userStore,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Always return success to prevent email enumeration
        if (string.IsNullOrWhiteSpace(request.Email))
            return Results.Ok(new { success = true });

        var user = await userStore.FindByEmailAsync(request.Email, ct);
        if (user is null)
        {
            logger.LogInformation("Password reset requested for non-existent email: {Email}", request.Email);
            // Artificial delay to prevent timing-based email enumeration
            await Task.Delay(TimeSpan.FromMilliseconds(100 + RandomNumberGenerator.GetInt32(200)), ct);
            return Results.Ok(new { success = true });
        }

        // Generate a reset token: random bytes + tie to security stamp
        var resetToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Update security stamp to include reset context
        user.SecurityStamp = resetToken;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Encode: token||email||expiresAtUnixSeconds (1 hour expiry)
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = $"{resetToken}||{user.Email}||{expiresAt}";
        var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

        var issuer = configuration["Issuer"]!;
        var callbackUrl = $"{issuer}/reset-password?p={Uri.EscapeDataString(encodedPayload)}";

        try
        {
            await emailService.SendPasswordResetEmailAsync(user.Email, callbackUrl, ct);
            logger.LogInformation("Password reset email sent to {Email}", user.Email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
        }

        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IUserStore userStore,
        IGrantStore grantStore,
        PasswordHasher passwordHasher,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Results.Json(new { error = "invalid_token", message = "Reset token is required." }, statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return Results.Json(new { error = "password_required", message = "Password is required." }, statusCode: 400);

        // Validate password strength
        var (isValid, validationError) = PasswordValidator.Validate(request.NewPassword);
        if (!isValid)
            return Results.Json(new { error = "weak_password", message = validationError }, statusCode: 400);

        // Decode token: base64(token||email)
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Token));
        }
        catch
        {
            return Results.Json(new { error = "invalid_token", message = "Invalid reset token format." }, statusCode: 400);
        }

        var parts = decoded.Split("||");
        if (parts.Length < 2)
            return Results.Json(new { error = "invalid_token", message = "Invalid reset token format." }, statusCode: 400);

        var resetToken = parts[0];
        var email = parts[1];

        // Validate expiration if present (tokens without expiry are legacy — reject them)
        if (parts.Length >= 3)
        {
            if (!long.TryParse(parts[2], out var expiresAtUnix) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            {
                return Results.Json(new { error = "token_expired", message = "This reset link has expired." }, statusCode: 400);
            }
        }
        else
        {
            return Results.Json(new { error = "invalid_token", message = "Invalid reset token format." }, statusCode: 400);
        }

        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
            return Results.Json(new { error = "invalid_token", message = "Invalid reset token." }, statusCode: 400);

        // Validate token matches security stamp
        if (user.SecurityStamp != resetToken)
            return Results.Json(new { error = "token_expired", message = "This reset link has already been used or has expired." }, statusCode: 400);

        // Reset password
        user.PasswordHash = passwordHasher.HashPassword(request.NewPassword);
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Invalidate all refresh tokens for this user
        await grantStore.RemoveAllBySubjectAsync(user.Id, ct);

        logger.LogInformation("Password reset completed for user {UserId} ({Email})", user.Id, user.Email);

        return Results.Ok(new { success = true });
    }

    private static IResult GetSessionAsync(HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var name = httpContext.User.FindFirstValue(ClaimTypes.Name);

        return Results.Ok(new
        {
            authenticated = true,
            userId,
            email,
            name
        });
    }

    private static async Task<IResult> SsoCheckAsync(
        string? email,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Results.Json(new { error = "email_required" }, statusCode: 400);

        var domain = email.Split('@', 2).LastOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
            return Results.Ok(new { ssoRequired = false });

        var ssoDomain = await ssoDomainStore.GetAsync(domain, ct);
        if (ssoDomain is null)
            return Results.Ok(new { ssoRequired = false });

        var redirectUrl = ssoDomain.ProviderType.Equals("oidc", StringComparison.OrdinalIgnoreCase)
            ? $"/oidc/{ssoDomain.ConnectionId}/login"
            : $"/saml/{ssoDomain.ConnectionId}/login";

        return Results.Ok(new
        {
            ssoRequired = true,
            providerType = ssoDomain.ProviderType,
            connectionId = ssoDomain.ConnectionId,
            redirectUrl
        });
    }
}

public sealed record LoginRequest(string? Email, string? Password);
public sealed record ForgotPasswordRequest(string? Email);
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);
