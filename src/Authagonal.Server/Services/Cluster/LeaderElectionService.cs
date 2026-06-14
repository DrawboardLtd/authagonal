using Authagonal.Core.Clustering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Maintains cluster leadership by acquiring/renewing a lease on a timer and publishing the result
/// to <see cref="LeaderElection"/>. With the in-process lease provider this node is always leader
/// (single node); with the Azure blob-lease provider exactly one node holds it, and leadership
/// transfers automatically when the holder stops renewing (lease expiry).
/// </summary>
public sealed class LeaderElectionService(
    ILeaseProvider leaseProvider,
    LeaderElection election,
    ClusterNode node,
    IOptions<ClusterOptions> options,
    ILogger<LeaderElectionService> logger) : BackgroundService
{
    private const string LeaderResource = "authagonal-leader";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseTtlSeconds));
        var renewInterval = TimeSpan.FromSeconds(Math.Max(2, ttl.TotalSeconds / 2));

        using var timer = new PeriodicTimer(renewInterval);

        // Run the first attempt immediately so single-node leadership is established at startup.
        do
        {
            try
            {
                var held = await leaseProvider.TryAcquireOrRenewAsync(LeaderResource, node.NodeId, ttl, stoppingToken)
                    .ConfigureAwait(false);
                election.Update(held);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // On any backend error, relinquish leadership locally to avoid two leaders.
                election.Update(false);
                logger.LogWarning(ex, "Leader lease renew failed; treating this node as non-leader this cycle");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        try
        {
            await leaseProvider.ReleaseAsync(LeaderResource, node.NodeId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort release on shutdown
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
