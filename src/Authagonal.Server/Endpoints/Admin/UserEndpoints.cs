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
        group.MapGet("/{userId}/exists", UserExists);
        group.MapPost("/", RegisterUser);
        group.MapPut("/", UpdateUser);
        group.MapDelete("/{userId}", DeleteUser);
        group.MapPost("/confirm-email", ConfirmEmail);
        group.MapPost("/{userId}/send-verification-email", SendVerificationEmail);
        group.MapPost("/{userId}/identities", LinkExternalIdentity);
        group.MapDelete("/{userId}/identities/{provider}/{externalUserId}", UnlinkExternalIdentity);
        // Bulk MFA-enrollment lookup for admin directory views ("uses MFA" badges). POST because
        // real directories exceed query-string id limits.
        group.MapPost("/mfa-status", GetMfaStatus);

        return app;
    }


    private sealed record MfaStatusRequest(List<string> UserIds);

    private static async Task<IResult> GetMfaStatus(
        MfaStatusRequest request,
        IMfaStore mfaStore,
        CancellationToken ct)
    {
        var statuses = new Dictionary<string, bool>();
        foreach (var userId in (request.UserIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(500))
        {
            var credentials = await mfaStore.GetCredentialsAsync(userId, ct);
            statuses[userId] = credentials.Count > 0;
        }
        return Results.Json(new { statuses });
    }

    private static async Task<IResult> GetUser(
        string userId,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var logins = await userStore.GetLoginsAsync(userId, ct);

        return TypedResults.Json(new UserDetailResponse
        {
            Id = user.Id,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            FirstName = user.FirstName,
            LastName = user.LastName,
            CompanyName = user.CompanyName,
            Phone = user.Phone,
            Locale = user.Locale,
            OrganizationId = user.OrganizationId,
            LockoutEnabled = user.LockoutEnabled,
            LockoutEnd = user.LockoutEnd,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
            ExternalLogins = logins.Select(l => new ExternalLoginDto
            {
                Provider = l.Provider,
                ProviderKey = l.ProviderKey,
                DisplayName = l.DisplayName
            })
        }, AuthagonalJsonContext.Default.UserDetailResponse);
    }

    private static async Task<IResult> UserExists(
        string userId,
        IUserStore userStore,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        return user is null ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> RegisterUser(
        RegisterUserRequest request,
        IUserStore userStore,
        IEnumerable<IAuthHook> authHooks,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        IEmailService emailService,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IProvisioningOrchestrator provisioning,
        IOptions<AuthOptions> authOptions,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_EmailRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.Password))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_PasswordRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var (isValid, validationError) = passwordValidator.Validate(request.Password, passwordPolicy);
        if (!isValid)
            return TypedResults.Json(new ErrorInfoResponse { Error = "weak_password", ErrorDescription = validationError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var existing = await userStore.FindByEmailAsync(request.Email, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_exists", ErrorDescription = localizer["Admin_UserExists"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        // Caller may supply the id (trusted admin endpoint, e.g. a bullclip-initiated creation that
        // keys the user by its own id); reject collisions. Otherwise generate one.
        string userId;
        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            if (await userStore.GetAsync(request.UserId, ct) is not null)
                return TypedResults.Json(new ErrorInfoResponse { Error = "user_id_in_use", ErrorDescription = localizer["Admin_UserIdInUse"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);
            userId = request.UserId;
        }
        else
        {
            userId = Guid.NewGuid().ToString("N");
        }

        var now = DateTimeOffset.UtcNow;
        var securityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var user = new AuthUser
        {
            Id = userId,
            Email = request.Email,
            NormalizedEmail = request.Email.ToUpperInvariant(),
            PasswordHash = passwordHasher.HashPassword(request.Password),
            EmailConfirmed = request.EmailConfirmed,
            FirstName = request.FirstName,
            LastName = request.LastName,
            CompanyName = request.CompanyName,
            OrganizationId = request.OrganizationId,
            Phone = request.Phone,
            Locale = request.Locale,
            LockoutEnabled = true,
            SecurityStamp = securityStamp,
            CreatedAt = now,
            CustomAttributes = request.CustomAttributes is { Count: > 0 }
                ? new Dictionary<string, string>(request.CustomAttributes)
                : [],
        };

        await userStore.CreateAsync(user, ct);

        try
        {
            await provisioning.ProvisionAsync(user, ct);
        }
        catch (ProvisioningException ex)
        {
            await userStore.DeleteAsync(user.Id, ct);
            return TypedResults.Json(new ErrorInfoResponse { Error = "provisioning_rejected", Message = ex.Message }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 422);
        }

        await authHooks.RunOnUserCreatedAsync(userId, request.Email, "admin", ct);

        // Send a verification email unless the admin created the user already confirmed.
        if (!user.EmailConfirmed)
        {
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
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_UserIdRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, request.UserId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var orgChanged = request.OrganizationId is not null &&
            !string.Equals(user.OrganizationId, request.OrganizationId, StringComparison.Ordinal);

        if (request.FirstName is not null) user.FirstName = request.FirstName;
        if (request.LastName is not null) user.LastName = request.LastName;
        if (request.CompanyName is not null) user.CompanyName = request.CompanyName;
        if (request.Phone is not null) user.Phone = request.Phone;
        if (request.Locale is not null) user.Locale = request.Locale;
        if (request.OrganizationId is not null) user.OrganizationId = request.OrganizationId;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Org change: rotate security stamp (invalidates cookies) and revoke all refresh tokens
        if (orgChanged)
        {
            user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await grantStore.RemoveAllBySubjectAsync(user.Id, ct);
        }

        await userStore.UpdateAsync(user, ct);
        await authHooks.RunOnUserUpdatedAsync(user.Id, user.Email, "admin", ct);

        return TypedResults.Json(new UserUpdateResponse
        {
            Id = user.Id,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            FirstName = user.FirstName,
            LastName = user.LastName,
            CompanyName = user.CompanyName,
            Phone = user.Phone,
            Locale = user.Locale,
            OrganizationId = user.OrganizationId,
            UpdatedAt = user.UpdatedAt ?? user.CreatedAt
        }, AuthagonalJsonContext.Default.UserUpdateResponse);
    }

    private static async Task<IResult> DeleteUser(
        string userId,
        IUserStore userStore,
        IGrantStore grantStore,
        IProvisioningOrchestrator provisioningOrchestrator,
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        // Remove all grants for this user
        await grantStore.RemoveAllBySubjectAsync(userId, ct);

        // Deprovision from all downstream apps (best-effort)
        await provisioningOrchestrator.DeprovisionAllAsync(userId, ct);

        await userStore.DeleteAsync(userId, ct);
        await authHooks.RunOnUserDeletedAsync(userId, user.Email, "admin", ct);

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
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_TokenRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch
        {
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_token", ErrorDescription = localizer["Admin_InvalidTokenFormat"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        var parts = decoded.Split("||");
        if (parts.Length < 2)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_token", ErrorDescription = localizer["Admin_InvalidTokenFormat"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var securityStamp = parts[0];
        var email = parts[1];

        // Validate expiration
        if (parts.Length >= 3)
        {
            if (!long.TryParse(parts[2], out var expiresAtUnix) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            {
                return TypedResults.Json(new ErrorInfoResponse { Error = "token_expired", ErrorDescription = localizer["Admin_VerificationExpired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
        }
        else
        {
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_token", ErrorDescription = localizer["Admin_InvalidTokenFormat"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = localizer["Admin_UserNotFoundSimple"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (user.SecurityStamp != securityStamp)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_token", ErrorDescription = localizer["Admin_TokenInvalidOrExpired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        user.EmailConfirmed = true;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await userStore.UpdateAsync(user, ct);

        return TypedResults.Json(new MessageResponse { Message = localizer["Auth_EmailConfirmed"].Value }, AuthagonalJsonContext.Default.MessageResponse);
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
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (user.EmailConfirmed)
            return TypedResults.Json(new ErrorInfoResponse { Error = "already_confirmed", ErrorDescription = localizer["Admin_EmailAlreadyConfirmed"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

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

        return TypedResults.Json(new MessageResponse { Message = localizer["Auth_VerificationSent"].Value }, AuthagonalJsonContext.Default.MessageResponse);
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
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ProviderKey))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_ProviderAndKeyRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

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
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        await userStore.RemoveLoginAsync(userId, provider, externalUserId, ct);

        return Results.NoContent();
    }

    // Request DTOs
    public sealed class RegisterUserRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string? FirstName { get; set; }
        public string? LastName { get; set; }

        /// <summary>
        /// Admin-only: caller-supplied user id (e.g. a bullclip-initiated creation that keys the user
        /// by its own id). Rejected with 409 if already in use; a fresh id is generated when omitted.
        /// </summary>
        public string? UserId { get; set; }

        /// <summary>Admin-only: create the user already email-confirmed, skipping the verification email.</summary>
        public bool EmailConfirmed { get; set; }

        public string? CompanyName { get; set; }
        public string? OrganizationId { get; set; }
        public string? Phone { get; set; }
        public string? Locale { get; set; }

        /// <summary>
        /// Arbitrary attributes persisted on the user and forwarded to provisioning targets
        /// (and emitted as scope-gated claims), mirroring the self-registration endpoint.
        /// </summary>
        public Dictionary<string, string>? CustomAttributes { get; set; }
    }

    public sealed class UpdateUserRequest
    {
        public string UserId { get; set; } = "";
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? CompanyName { get; set; }
        public string? Phone { get; set; }
        public string? Locale { get; set; }
        public string? OrganizationId { get; set; }
    }

    public sealed class ConfirmEmailRequest
    {
        public string Token { get; set; } = "";
    }

    public sealed class LinkExternalIdentityRequest
    {
        public string Provider { get; set; } = "";
        public string ProviderKey { get; set; } = "";
        public string? DisplayName { get; set; }
    }
}
