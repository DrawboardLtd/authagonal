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
            return Results.NotFound(new { error = "user_not_found" });

        var credentials = await mfaStore.GetCredentialsAsync(userId, ct);

        var methods = credentials.Select(c => new
        {
            id = c.Id,
            type = c.Type.ToString().ToLowerInvariant(),
            name = c.Name,
            createdAt = c.CreatedAt,
            lastUsedAt = c.LastUsedAt,
            isConsumed = c.Type == MfaCredentialType.RecoveryCode ? c.IsConsumed : (bool?)null,
        }).ToList();

        return Results.Ok(new { enabled = user.MfaEnabled, methods });
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
            return Results.NotFound(new { error = "user_not_found" });

        await mfaStore.DeleteAllCredentialsAsync(userId, ct);

        if (user.MfaEnabled)
        {
            user.MfaEnabled = false;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        logger.LogInformation("MFA reset for user {UserId} via admin API", userId);

        return Results.Ok(new { success = true });
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
            return Results.NotFound(new { error = "user_not_found" });

        var cred = await mfaStore.GetCredentialAsync(userId, credentialId, ct);
        if (cred is null)
            return Results.NotFound(new { error = "credential_not_found" });

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

        return Results.Ok(new { success = true });
    }
}
