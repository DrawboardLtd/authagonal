using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
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

        // Directory reads. The store has indexed these all along — email, email-prefix and name
        // prefix — but nothing exposed them, so an admin console had no way to find a person except
        // by already knowing their id.
        group.MapGet("/search", SearchUsers);
        group.MapGet("/by-email", GetUserByEmail);
        group.MapGet("/", ListUsers);
        group.MapPost("/exists", UsersExist);

        group.MapGet("/{userId}", GetUser);
        group.MapGet("/{userId}/exists", UserExists);
        group.MapPost("/{userId}/set-password", SetPassword);
        group.MapPost("/{userId}/unlock", UnlockUser);
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
        const int MaxIds = 500;
        var ids = (request.UserIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var truncated = ids.Count > MaxIds;
        if (truncated)
            ids = ids.GetRange(0, MaxIds);

        // Bounded-parallel reads: a 500-id directory batch was N sequential store round-trips.
        var statuses = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (userId, token) =>
            {
                var credentials = await mfaStore.GetCredentialsAsync(userId, token);
                statuses[userId] = credentials.Count > 0;
            });

        // `truncated` tells the caller its request was capped, instead of silently returning 500 of 600.
        return Results.Json(new { statuses = new Dictionary<string, bool>(statuses), truncated });
    }

    private static async Task<IResult> SearchUsers(
        string q,
        int? maxResults,
        IUserStore userStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return TypedResults.Json(new UserSearchResponse(), AuthagonalJsonContext.Default.UserSearchResponse);

        // Clamped. maxResults reached the store unbounded, so one admin-scoped request could ask for
        // the entire directory in a single page — a memory and egress amplifier on a table that holds
        // every user's PII, and the kind of request a compromised admin token makes first.
        var pageSize = Math.Clamp(maxResults ?? 20, 1, 200);
        var users = await userStore.SearchAsync(q.Trim(), pageSize, ct);
        return TypedResults.Json(new UserSearchResponse { Users = users.Select(Summarize).ToList() },
            AuthagonalJsonContext.Default.UserSearchResponse);
    }

    /// <summary>
    /// Exact lookup by email. Distinct from <c>search</c>, which is a prefix match and may return
    /// several people — a caller resolving "this address" to "this account" wants one answer or none.
    /// </summary>
    private static async Task<IResult> GetUserByEmail(
        string email,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "email is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var user = await userStore.FindByEmailAsync(email.Trim(), ct);
        return user is null
            ? TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, email) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404)
            : TypedResults.Json(Summarize(user), AuthagonalJsonContext.Default.UserSummary);
    }

    /// <summary>
    /// Cursor-paged directory listing. Cursors rather than offsets because the underlying store pages
    /// by continuation token — an offset would re-scan from the start on every page.
    /// </summary>
    private static async Task<IResult> ListUsers(
        string? organizationId,
        int? count,
        string? continuationToken,
        IUserStore userStore,
        CancellationToken ct)
    {
        // Clamped, like search. count reached the store unbounded, so ?count=10000000 asked one request to
        // read — and, with field encryption on, decrypt — the entire directory into memory before
        // answering. The continuation token is how a caller gets more than a page.
        var page = await userStore.ListPageAsync(organizationId, Math.Clamp(count ?? 100, 1, 200), continuationToken, ct);
        return TypedResults.Json(new UserListResponse
        {
            Users = page.Users.Select(Summarize).ToList(),
            ContinuationToken = page.ContinuationToken,
        }, AuthagonalJsonContext.Default.UserListResponse);
    }

    /// <summary>
    /// Of the given ids, which exist. POST because a reconciliation batch exceeds query-string
    /// limits, and because the caller is asking about a set rather than fetching a resource.
    /// </summary>
    private static async Task<IResult> UsersExist(
        UserIdsRequest request,
        IUserStore userStore,
        CancellationToken ct)
    {
        const int MaxIds = 500;
        var ids = (request.UserIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var truncated = ids.Count > MaxIds;
        if (truncated) ids = ids.GetRange(0, MaxIds);

        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (userId, token) =>
            {
                if (await userStore.ExistsAsync(userId, token)) found.Add(userId);
            });

        // truncated tells the caller its request was capped, rather than silently answering about 500 of 600.
        return TypedResults.Json(new UserExistsResponse { UserIds = [.. found], Truncated = truncated },
            AuthagonalJsonContext.Default.UserExistsResponse);
    }

    /// <summary>
    /// Set a user's password on their behalf — the support path for someone locked out of an account
    /// with no working address to send a reset to.
    /// </summary>
    /// <remarks>
    /// Revokes every refresh token and rotates the security stamp. A password change that leaves the
    /// old sessions running has not changed who can act as that person, which is the entire point of
    /// changing it.
    /// </remarks>
    private static async Task<IResult> SetPassword(
        string userId,
        SetPasswordRequest request,
        IUserStore userStore,
        IGrantStore grantStore,
        IRevokedTokenStore? revokedTokenStore,
        PasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        PasswordPolicy passwordPolicy,
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_PasswordRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var (isValid, validationError) = passwordValidator.Validate(request.Password, passwordPolicy);
        if (!isValid)
            return TypedResults.Json(new ErrorInfoResponse { Error = "weak_password", ErrorDescription = validationError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        user.PasswordHash = passwordHasher.HashPassword(request.Password);
        user.PendingPasswordHash = null;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);
        // Both halves: the grant rows AND the access tokens they minted. Removing the rows alone left the
        // old password's tokens working for up to AccessTokenLifetimeSeconds after the reset.
        await GrantRevocation.RevokeAllSubjectGrantsAsync(grantStore, revokedTokenStore, user.Id, null, ct);
        await authHooks.RunOnPasswordChangedAsync(user.Id, user.Email, "admin", ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.password_set", "user", userId, null, ct);
        return Results.NoContent();
    }

    /// <summary>Clear a lockout and its failed-attempt count, letting someone back in now rather than
    /// when the lockout happens to expire.</summary>
    private static async Task<IResult> UnlockUser(
        string userId,
        IUserStore userStore,
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);
        await authHooks.RunOnUserUpdatedAsync(user.Id, user.Email, "admin", ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.unlocked", "user", userId, null, ct);
        return Results.NoContent();
    }

    private static UserSummary Summarize(AuthUser user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        EmailConfirmed = user.EmailConfirmed,
        FirstName = user.FirstName,
        LastName = user.LastName,
        OrganizationId = user.OrganizationId,
        IsActive = user.IsActive,
        LockoutEnd = user.LockoutEnd,
        MfaEnabled = user.MfaEnabled,
        Roles = user.Roles,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt,
    };

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
            AccessFailedCount = user.AccessFailedCount,
            // Presence only — the hash never leaves the store.
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            IsActive = user.IsActive,
            Roles = user.Roles,
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
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_EmailRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.Password))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = localizer["Admin_PasswordRequired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        // The address becomes a storage key. With the default non-tokenizing configuration the normalized
        // address IS the email index's PartitionKey, and CreateAsync writes the profile row BEFORE the index
        // row — so a key the storage service rejects (Azure Table refuses '/', '\\', '#', '?') fails after the
        // account is durably created, leaving a record FindByEmailAsync cannot reach: the holder cannot log in,
        // cannot reset their password, and the address cannot be reused because the profile row still has it.
        //
        // Both sibling creation paths — anonymous self-registration and SCIM — already refuse these values for
        // exactly that reason. This one checked only that the field was non-empty, and it is the path a stolen
        // admin token reaches, which the audit comments in this file already treat as the threat model.
        if (!StorageKeySafety.IsPlausibleEmail(request.Email))
            return TypedResults.Json(
                new ErrorInfoResponse
                {
                    Error = "invalid_request",
                    ErrorDescription = "email must be a valid email address",
                }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

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

        // SkipProvisioning is for the case where the CALLER is itself the provisioning target. A
        // first-party app creating an identity for a user it is already mid-way through setting up
        // does not want its own callback re-entered: it would be asked to provision a user it is in
        // the middle of provisioning, carrying only whatever attributes survived the round trip.
        if (!request.SkipProvisioning)
        {
            try
            {
                await provisioning.ProvisionAsync(user, ct);
            }
            catch (ProvisioningException ex)
            {
                await userStore.DeleteAsync(user.Id, ct);
                return TypedResults.Json(new ErrorInfoResponse { Error = "provisioning_rejected", Message = ex.Message }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 422);
            }
        }

        await authHooks.RunOnUserCreatedAsync(userId, request.Email, "admin", ct);

        // Send a verification email unless the admin created the user already confirmed.
        if (!user.EmailConfirmed)
        {
            var issuer = tenantContext.Issuer;
            var expiresAt = DateTimeOffset.UtcNow.AddHours(authOptions.Value.EmailVerificationExpiryHours).ToUnixTimeSeconds();
            var tokenData = $"{securityStamp}||{user.Email}||{expiresAt}";
            var encodedToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tokenData));
            // The PUBLIC confirmation endpoint. /api/v1/profile/confirm-email is POST-only AND behind
            // RequireAuthorization("IdentityAdmin"), so an emailed link to it could never work: the click
            // is an anonymous GET. The token is the authorization here — it is bound to the security
            // stamp — and the public endpoint understands the identical token format.
            var callbackUrl = $"{issuer}/api/auth/confirm-email?token={Uri.EscapeDataString(encodedToken)}";

            try
            {
                await emailService.SendVerificationEmailAsync(user.Email, callbackUrl, ct);
            }
            catch
            {
                // Don't fail registration if email sending fails
            }
        }

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.created", "user", userId, user.Email, ct);
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
        IRevokedTokenStore? revokedTokenStore,
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
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

        var emailConfirmed = request.EmailConfirmed ?? request.EmailVerified;
        if (emailConfirmed is not null) user.EmailConfirmed = emailConfirmed.Value;

        var deactivated = request.IsActive is false && user.IsActive;
        if (request.IsActive is not null) user.IsActive = request.IsActive.Value;

        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Org change: rotate security stamp (invalidates cookies) and revoke all refresh tokens.
        // Deactivation gets the same treatment for a stronger reason: a disabled account that keeps
        // working until its token expires has not been disabled.
        if (orgChanged || deactivated)
        {
            user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            // Through GrantRevocation: the comment above is only true if the access tokens go too. Rotating
            // the stamp kills the cookie and removing the rows kills the refresh token, but the access token
            // already issued is a self-contained JWT — it kept passing the JwtBearer scheme, kept returning
            // the user's claims from /connect/userinfo and kept reporting active:true from /connect/introspect
            // for its full lifetime. A disabled account that keeps working until its token expires has not
            // been disabled.
            await GrantRevocation.RevokeAllSubjectGrantsAsync(grantStore, revokedTokenStore, user.Id, null, ct);
        }

        await userStore.UpdateAsync(user, ct);
        await authHooks.RunOnUserUpdatedAsync(user.Id, user.Email, "admin", ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.updated", "user", user.Id, null, ct);
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
        IRevokedTokenStore? revokedTokenStore,
        IProvisioningOrchestrator provisioningOrchestrator,
        IEnumerable<IAuthHook> authHooks,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
        IMfaStore? mfaStore,
        IScimGroupStore? scimGroupStore,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        // Remove all grants for this user, and the access tokens they minted — a deleted account whose
        // token still resolves is not deleted.
        await GrantRevocation.RevokeAllSubjectGrantsAsync(grantStore, revokedTokenStore, userId, null, ct);

        // Deprovision from all downstream apps (best-effort)
        await provisioningOrchestrator.DeprovisionAllAsync(userId, ct);

        // Second factors and group memberships are keyed on the user id, and the id can come back — the admin
        // create endpoint accepts a caller-supplied UserId, and SCIM reclaims a tombstoned row in place. A
        // passkey left behind is a way back into the NEXT account at this id, and the passwordless sign-in path
        // resolves the account from the credential without consulting MfaEnabled. See AccountArtefactPurge.
        //
        // Before the delete, so a failure here leaves the account intact rather than deleted-but-credentialed.
        await AccountArtefactPurge.PurgeAsync(userId, mfaStore, scimGroupStore, ct);

        await userStore.DeleteAsync(userId, ct);
        await authHooks.RunOnUserDeletedAsync(userId, user.Email, "admin", ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.deleted", "user", userId, null, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmEmail(
        HttpContext httpContext,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
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

        // Fixed-time — see the matching check in AuthEndpoints.ConfirmEmailAsync. The stamp is the
        // whole of the authorisation for this state change and the token carries no MAC.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(user.SecurityStamp ?? ""), Encoding.UTF8.GetBytes(securityStamp)))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_token", ErrorDescription = localizer["Admin_TokenInvalidOrExpired"].Value }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        user.EmailConfirmed = true;
        user.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await userStore.UpdateAsync(user, ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.email_confirmed", "user", user.Id, user.Email, ct);
        return TypedResults.Json(new MessageResponse { Message = localizer["Auth_EmailConfirmed"].Value }, AuthagonalJsonContext.Default.MessageResponse);
    }

    private static async Task<IResult> SendVerificationEmail(
        string userId,
        IUserStore userStore,
        IEmailService emailService,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IOptions<AuthOptions> authOptions,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
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
        // The PUBLIC confirmation endpoint. /api/v1/profile/confirm-email is POST-only AND behind
            // RequireAuthorization("IdentityAdmin"), so an emailed link to it could never work: the click
            // is an anonymous GET. The token is the authorization here — it is bound to the security
            // stamp — and the public endpoint understands the identical token format.
            var callbackUrl = $"{issuer}/api/auth/confirm-email?token={Uri.EscapeDataString(encodedToken)}";

        await emailService.SendVerificationEmailAsync(user.Email, callbackUrl, ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.verification_resent", "user", userId, null, ct);
        return TypedResults.Json(new MessageResponse { Message = localizer["Auth_VerificationSent"].Value }, AuthagonalJsonContext.Default.MessageResponse);
    }

    private static async Task<IResult> LinkExternalIdentity(
        string userId,
        LinkExternalIdentityRequest request,
        IUserStore userStore,
        IStringLocalizer<SharedMessages> localizer,
        IAuditLogger audit,
        HttpContext httpContext,
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

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.identity_linked", "user", userId, $"{request.Provider}|{request.ProviderKey}", ct);
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
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = string.Format(localizer["Admin_UserNotFound"].Value, userId) }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        await userStore.RemoveLoginAsync(userId, provider, externalUserId, ct);

        // Audited: an admin write on someone else's account, and this group produced no trail at all.
        await audit.LogAsync(AdminActor.Of(httpContext), "user.identity_unlinked", "user", userId, $"{provider}|{externalUserId}", ct);
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
        /// Admin-only: caller-supplied user id (e.g. a first-party-initiated creation that keys the user
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

        /// <summary>
        /// Admin-only: create the user WITHOUT running provisioning.
        /// </summary>
        /// <remarks>
        /// For a first-party app that is itself a provisioning target and is already part-way through
        /// setting this user up — it is calling here to mint the identity, not to be called back about
        /// a user it is in the middle of creating. Without this, that app receives its own Try for a
        /// half-built user carrying only the attributes that survived the round trip.
        /// </remarks>
        public bool SkipProvisioning { get; set; }
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

        /// <summary>
        /// Deactivate or reactivate the account. Null leaves it alone.
        /// </summary>
        /// <remarks>
        /// Deactivating rotates the security stamp (which ends the cookie session), removes every grant,
        /// AND revokes the access tokens those grants minted — a self-contained JWT survives the removal of
        /// its grant row, so without that last part a blocked account kept calling resource servers for the
        /// rest of its access-token lifetime. A block that only takes effect at next login is not a block.
        /// </remarks>
        public bool? IsActive { get; set; }

        /// <summary>
        /// Mark the address confirmed (or not). Null leaves it alone. Admin-only, for the case where
        /// possession has been established some other way and the verification email is noise.
        /// </summary>
        public bool? EmailConfirmed { get; set; }

        /// <summary>Accepted as an alias for <see cref="EmailConfirmed"/> — the field callers have
        /// been sending all along, which was silently dropped until it was named here.</summary>
        public bool? EmailVerified { get; set; }
    }

    public sealed class SetPasswordRequest
    {
        public string Password { get; set; } = "";
    }

    public sealed class UserIdsRequest
    {
        public List<string>? UserIds { get; set; }
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
