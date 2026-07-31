using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class MfaAdminEndpoints
{
    public static IEndpointRouteBuilder MapMfaAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/profile/{userId}/mfa")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - MFA");

        group.MapGet("/", GetMfaStatus);
        group.MapDelete("/", ResetMfa);
        group.MapDelete("/{credentialId}", DeleteCredential);

        return app;
    }

    private static async Task<IResult> GetMfaStatus(
        string userId,
        IUserStore userStore,
        IMfaStore mfaStore,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var credentials = await mfaStore.GetCredentialsAsync(userId, ct);

        var methods = credentials.Select(c => new MfaMethodInfo
        {
            Id = c.Id,
            Type = c.Type.ToString().ToLowerInvariant(),
            Name = c.Name,
            CreatedAt = c.CreatedAt,
            LastUsedAt = c.LastUsedAt,
            IsConsumed = c.Type == MfaCredentialType.RecoveryCode ? c.IsConsumed : null,
        }).ToList();

        return TypedResults.Json(new MfaStatusResponse { Enabled = user.MfaEnabled, Methods = methods }, AuthagonalJsonContext.Default.MfaStatusResponse);
    }

    /// <remarks>
    /// Stripping every second factor from an arbitrary account in one call is the strongest
    /// account-takeover primitive in the product, and it was the quietest: no <see cref="IAuthHook"/>
    /// member fired, so nothing reached a host's audit sink, SIEM or user-notification pipeline —
    /// while the self-service equivalent has always raised OnMfaCredentialRemoved. The only trace was
    /// a log line naming the target and not the acting admin.
    /// <para>
    /// It also left the victim's live sessions alone. Cookie validation revalidates against the
    /// security stamp, so without rotating it every existing <c>mfa_authenticated</c> session for the
    /// target stayed valid — including an attacker's, which is the session an admin resetting MFA is
    /// most likely trying to cut off.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ResetMfa(
        string userId,
        HttpContext httpContext,
        IUserStore userStore,
        IMfaStore mfaStore,
        IEnumerable<IAuthHook> authHooks,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var removed = await mfaStore.GetCredentialsAsync(userId, ct);
        await mfaStore.DeleteAllCredentialsAsync(userId, ct);

        user.MfaEnabled = false;
        // Invalidates every existing session for the target, including one established with the
        // factors just removed.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        foreach (var cred in removed)
        {
            await authHooks.RunOnMfaCredentialRemovedAsync(
                userId, user.Email, cred.Type.ToString().ToLowerInvariant(), mfaDisabled: true, ct);
        }

        logger.LogWarning(
            "MFA reset for user {UserId} by admin {AdminSubject} via admin API; {Count} credential(s) removed and sessions invalidated",
            userId, ActingAdmin(httpContext), removed.Count);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    /// <summary>The acting administrator's subject, so the audit trail names who did this.</summary>
    private static string ActingAdmin(HttpContext httpContext) =>
        httpContext.User.FindFirstValue("sub")
        ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? httpContext.User.FindFirstValue("client_id")
        ?? "unknown";

    private static async Task<IResult> DeleteCredential(
        string userId,
        string credentialId,
        HttpContext httpContext,
        IUserStore userStore,
        IMfaStore mfaStore,
        IEnumerable<IAuthHook> authHooks,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var cred = await mfaStore.GetCredentialAsync(userId, credentialId, ct);
        if (cred is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "credential_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        await mfaStore.DeleteCredentialAsync(userId, credentialId, ct);

        // Check if user still has MFA credentials (excluding recovery codes)
        var remaining = await mfaStore.GetCredentialsAsync(userId, ct);
        var mfaDisabled = !remaining.Any(c => c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn);

        if (mfaDisabled)
        {
            user.MfaEnabled = false;
            // Removing the last factor is a reset in all but name, so it invalidates sessions for
            // the same reason ResetMfa does.
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        await authHooks.RunOnMfaCredentialRemovedAsync(
            userId, user.Email, cred.Type.ToString().ToLowerInvariant(), mfaDisabled, ct);

        logger.LogWarning(
            "MFA credential {CredentialId} ({Type}) removed from user {UserId} by admin {AdminSubject} via admin API",
            credentialId, cred.Type, userId, ActingAdmin(httpContext));

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }
}
