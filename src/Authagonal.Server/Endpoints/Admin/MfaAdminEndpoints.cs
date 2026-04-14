using Authagonal.Core.Models;
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

    private static async Task<IResult> ResetMfa(
        string userId,
        IUserStore userStore,
        IMfaStore mfaStore,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        await mfaStore.DeleteAllCredentialsAsync(userId, ct);

        if (user.MfaEnabled)
        {
            user.MfaEnabled = false;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        logger.LogInformation("MFA reset for user {UserId} via admin API", userId);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> DeleteCredential(
        string userId,
        string credentialId,
        IUserStore userStore,
        IMfaStore mfaStore,
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
        if (!remaining.Any(c => c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn))
        {
            if (user.MfaEnabled)
            {
                user.MfaEnabled = false;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user, ct);
            }
        }

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }
}
