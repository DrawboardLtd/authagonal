using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Endpoints.Admin;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/profile")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Users");

        group.MapGet("/{userId}", GetUser);
        group.MapPost("/", RegisterUser);
        group.MapPut("/", UpdateUser);
        group.MapDelete("/{userId}", DeleteUser);
        group.MapPost("/confirm-email", ConfirmEmail);
        group.MapPost("/{userId}/send-verification-email", SendVerificationEmail);
        group.MapPost("/{userId}/identities", LinkExternalIdentity);
        group.MapDelete("/{userId}/identities/{provider}/{externalUserId}", UnlinkExternalIdentity);

        return app;
    }

    private static async Task<IResult> GetUser(
        string userId,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, userId) });

        var logins = await userStore.GetLoginsAsync(userId, ct);

        return Results.Ok(new
        {
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.FirstName,
            user.LastName,
            user.CompanyName,
            user.Phone,
            user.OrganizationId,
            user.LockoutEnabled,
            user.LockoutEnd,
            user.CreatedAt,
            user.UpdatedAt,
            ExternalLogins = logins.Select(l => new
            {
                l.Provider,
                l.ProviderKey,
                l.DisplayName
            })
        });
    }

    private static async Task<IResult> RegisterUser(
        RegisterUserRequest request,
        IUserStore userStore,
        IAuthHook authHook,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        IEmailService emailService,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IOptions<AuthOptions> authOptions,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Results.BadRequest(new { error = "invalid_request", error_description = localizer["Admin_EmailRequired"].Value });

        if (string.IsNullOrWhiteSpace(request.Password))
            return Results.BadRequest(new { error = "invalid_request", error_description = localizer["Admin_PasswordRequired"].Value });

        var (isValid, validationError) = passwordValidator.Validate(request.Password, passwordPolicy);
        if (!isValid)
            return Results.BadRequest(new { error = "weak_password", error_description = validationError });

        var existing = await userStore.FindByEmailAsync(request.Email, ct);
        if (existing is not null)
            return Results.Conflict(new { error = "user_exists", error_description = localizer["Admin_UserExists"].Value });

        var userId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var securityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var user = new AuthUser
        {
            Id = userId,
            Email = request.Email,
            NormalizedEmail = request.Email.ToUpperInvariant(),
            PasswordHash = passwordHasher.HashPassword(request.Password),
            EmailConfirmed = false,
            FirstName = request.FirstName,
            LastName = request.LastName,
            LockoutEnabled = true,
            SecurityStamp = securityStamp,
            CreatedAt = now
        };

        await userStore.CreateAsync(user, ct);
        await authHook.OnUserCreatedAsync(userId, request.Email, "admin", ct);

        // Send verification email
        var issuer = tenantContext.Issuer;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(authOptions.Value.EmailVerificationExpiryHours).ToUnixTimeSeconds();
        var tokenData = $"{securityStamp}||{user.Email}||{expiresAt}";
        var encodedToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tokenData));
        var callbackUrl = $"{issuer}/api/v1/profile/confirm-email?token={Uri.EscapeDataString(encodedToken)}";

        try
        {
            await emailService.SendVerificationEmailAsync(user.Email, callbackUrl, ct);
        }
        catch
        {
            // Don't fail registration if email sending fails
        }

        return Results.Created($"/api/v1/profile/{userId}", new
        {
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.FirstName,
            user.LastName,
            user.CreatedAt
        });
    }

    private static async Task<IResult> UpdateUser(
        UpdateUserRequest request,
        IUserStore userStore,
        IGrantStore grantStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new { error = "invalid_request", error_description = localizer["Admin_UserIdRequired"].Value });

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, request.UserId) });

        var orgChanged = request.OrganizationId is not null &&
            !string.Equals(user.OrganizationId, request.OrganizationId, StringComparison.Ordinal);

        if (request.FirstName is not null) user.FirstName = request.FirstName;
        if (request.LastName is not null) user.LastName = request.LastName;
        if (request.CompanyName is not null) user.CompanyName = request.CompanyName;
        if (request.Phone is not null) user.Phone = request.Phone;
        if (request.OrganizationId is not null) user.OrganizationId = request.OrganizationId;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Org change: rotate security stamp (invalidates cookies) and revoke all refresh tokens
        if (orgChanged)
        {
            user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await grantStore.RemoveAllBySubjectAsync(user.Id, ct);
        }

        await userStore.UpdateAsync(user, ct);

        return Results.Ok(new
        {
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.FirstName,
            user.LastName,
            user.CompanyName,
            user.Phone,
            user.OrganizationId,
            user.UpdatedAt
        });
    }

    private static async Task<IResult> DeleteUser(
        string userId,
        IUserStore userStore,
        IGrantStore grantStore,
        IProvisioningOrchestrator provisioningOrchestrator,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, userId) });

        // Remove all grants for this user
        await grantStore.RemoveAllBySubjectAsync(userId, ct);

        // Deprovision from all downstream apps (best-effort)
        await provisioningOrchestrator.DeprovisionAllAsync(userId, ct);

        await userStore.DeleteAsync(userId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmEmail(
        HttpContext httpContext,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var token = httpContext.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            // Also check body
            if (httpContext.Request.HasJsonContentType())
            {
                var body = await httpContext.Request.ReadFromJsonAsync<ConfirmEmailRequest>(ct);
                token = body?.Token;
            }
        }

        if (string.IsNullOrWhiteSpace(token))
            return Results.BadRequest(new { error = "invalid_request", error_description = localizer["Admin_TokenRequired"].Value });

        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch
        {
            return Results.BadRequest(new { error = "invalid_token", error_description = localizer["Admin_InvalidTokenFormat"].Value });
        }

        var parts = decoded.Split("||");
        if (parts.Length < 2)
            return Results.BadRequest(new { error = "invalid_token", error_description = localizer["Admin_InvalidTokenFormat"].Value });

        var securityStamp = parts[0];
        var email = parts[1];

        // Validate expiration
        if (parts.Length >= 3)
        {
            if (!long.TryParse(parts[2], out var expiresAtUnix) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            {
                return Results.BadRequest(new { error = "token_expired", error_description = localizer["Admin_VerificationExpired"].Value });
            }
        }
        else
        {
            return Results.BadRequest(new { error = "invalid_token", error_description = localizer["Admin_InvalidTokenFormat"].Value });
        }

        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = localizer["Admin_UserNotFoundSimple"].Value });

        if (user.SecurityStamp != securityStamp)
            return Results.BadRequest(new { error = "invalid_token", error_description = localizer["Admin_TokenInvalidOrExpired"].Value });

        user.EmailConfirmed = true;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await userStore.UpdateAsync(user, ct);

        return Results.Ok(new { message = localizer["Auth_EmailConfirmed"].Value });
    }

    private static async Task<IResult> SendVerificationEmail(
        string userId,
        IUserStore userStore,
        IEmailService emailService,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IOptions<AuthOptions> authOptions,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, userId) });

        if (user.EmailConfirmed)
            return Results.BadRequest(new { error = "already_confirmed", error_description = localizer["Admin_EmailAlreadyConfirmed"].Value });

        // Rotate security stamp for new token
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        var issuer = tenantContext.Issuer;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(authOptions.Value.EmailVerificationExpiryHours).ToUnixTimeSeconds();
        var tokenData = $"{user.SecurityStamp}||{user.Email}||{expiresAt}";
        var encodedToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tokenData));
        var callbackUrl = $"{issuer}/api/v1/profile/confirm-email?token={Uri.EscapeDataString(encodedToken)}";

        await emailService.SendVerificationEmailAsync(user.Email, callbackUrl, ct);

        return Results.Ok(new { message = localizer["Auth_VerificationSent"].Value });
    }

    private static async Task<IResult> LinkExternalIdentity(
        string userId,
        LinkExternalIdentityRequest request,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, userId) });

        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ProviderKey))
            return Results.BadRequest(new { error = "invalid_request", error_description = localizer["Admin_ProviderAndKeyRequired"].Value });

        var login = new ExternalLoginInfo
        {
            UserId = userId,
            Provider = request.Provider,
            ProviderKey = request.ProviderKey,
            DisplayName = request.DisplayName
        };

        await userStore.AddLoginAsync(login, ct);

        return Results.Created($"/api/v1/profile/{userId}/identities", new
        {
            login.Provider,
            login.ProviderKey,
            login.DisplayName
        });
    }

    private static async Task<IResult> UnlinkExternalIdentity(
        string userId,
        string provider,
        string externalUserId,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = string.Format(localizer["Admin_UserNotFound"].Value, userId) });

        await userStore.RemoveLoginAsync(userId, provider, externalUserId, ct);

        return Results.NoContent();
    }

    // Request DTOs
    public sealed record RegisterUserRequest(
        string Email,
        string Password,
        string? FirstName,
        string? LastName);

    public sealed record UpdateUserRequest(
        string UserId,
        string? FirstName,
        string? LastName,
        string? CompanyName,
        string? Phone,
        string? OrganizationId);

    public sealed record ConfirmEmailRequest(string Token);

    public sealed record LinkExternalIdentityRequest(
        string Provider,
        string ProviderKey,
        string? DisplayName);
}
