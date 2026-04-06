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

                await using var scope = scopeFactory.CreateAsyncScope();
                var keyStore = scope.ServiceProvider.GetRequiredService<ISigningKeyStore>();

                var rotated = await SigningKeyOps.CheckAndRotateAsync(
                    keyStore, options.SigningKeyLifetimeDays, options.KeyRotationLeadTimeDays,
                    logger, stoppingToken);

                if (rotated)
                    await keyManager.ForceRefreshAsync(stoppingToken);
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
}
