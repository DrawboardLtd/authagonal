using Authagonal.Core.Stores;
using Authagonal.Server.Services.Cluster;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Periodically checks whether the active signing key is approaching expiry and rotates it.
/// Only the cluster leader performs the rotation to avoid concurrent key generation.
/// Disabled by default — enable via <c>Auth:KeyRotationEnabled = true</c>.
/// </summary>
public sealed class SigningKeyRotationService(
    IServiceScopeFactory scopeFactory,
    ClusterLeaderService leaderService,
    KeyManager keyManager,
    IOptions<AuthOptions> authOptions,
    ILogger<SigningKeyRotationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = authOptions.Value;

        if (!options.KeyRotationEnabled)
        {
            logger.LogInformation("Signing key rotation is disabled");
            return;
        }

        // Wait before first check to let the cluster stabilise
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(options.KeyRotationCheckIntervalMinutes), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.KeyRotationCheckIntervalMinutes));

        do
        {
            try
            {
                if (!leaderService.IsLeader())
                {
                    logger.LogDebug("Skipping key rotation check — not the cluster leader");
                    continue;
                }

                await CheckAndRotateAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during signing key rotation check");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAndRotateAsync(AuthOptions options, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var keyStore = scope.ServiceProvider.GetRequiredService<ISigningKeyStore>();

        var activeKey = await keyStore.GetActiveKeyAsync(ct);
        if (activeKey is null)
        {
            logger.LogWarning("No active signing key found — KeyManager will generate one on next refresh");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var timeUntilExpiry = activeKey.ExpiresAt - now;
        var rotationThreshold = TimeSpan.FromDays(options.KeyRotationLeadTimeDays);

        if (timeUntilExpiry > rotationThreshold)
        {
            logger.LogDebug(
                "Active key {KeyId} expires in {Days:F0} days — no rotation needed (threshold: {Threshold} days)",
                activeKey.KeyId, timeUntilExpiry.TotalDays, options.KeyRotationLeadTimeDays);
            return;
        }

        logger.LogInformation(
            "Active key {KeyId} expires in {Days:F0} days (threshold: {Threshold} days). Rotating",
            activeKey.KeyId, timeUntilExpiry.TotalDays, options.KeyRotationLeadTimeDays);

        // Deactivate the old key (it stays in JWKS until it expires for token validation)
        await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);

        // KeyManager's next refresh will detect no active key and generate a new one.
        // Force an immediate refresh so the new key is live within seconds.
        await keyManager.ForceRefreshAsync(ct);

        logger.LogInformation("Signing key rotated. Old key {OldKeyId} deactivated", activeKey.KeyId);
    }
}
