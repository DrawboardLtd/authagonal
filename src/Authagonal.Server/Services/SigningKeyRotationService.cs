using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services.Cluster;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Periodically checks whether the active signing key is approaching expiry and DEACTIVATES it, on
/// the cluster leader only. Disabled by default — enable via <c>Auth:KeyRotationEnabled = true</c>.
/// </summary>
/// <remarks>
/// This used to say it was what stopped concurrent key generation. It is not: this service never
/// generates. Generation happens in <c>ProtocolSigningKeyOps.EnsureActiveKeyAsync</c>, which every
/// node runs at startup and on each cache refresh — and because <c>KeyRotationEnabled</c> defaults to
/// false, in the default configuration rollover at expiry is driven entirely by that path, with this
/// service not even running. Generation is single-writer under its own cluster lease; see there.
/// </remarks>
public sealed class SigningKeyRotationService(
    IServiceScopeFactory scopeFactory,
    ClusterLeaderService leaderService,
    ProtocolKeyManager keyManager,
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

                var rotated = await ProtocolSigningKeyOps.CheckAndRotateAsync(
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
