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

    /// <summary>Longest lease any supported backend actually honours (Azure blob leases cap at 60s).</summary>
    private static readonly TimeSpan MaxLeaseTtl = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cluster:Enabled was documented as the master switch — "when false the node runs standalone
        // (always leader, in-process event bus)" — and read nowhere, so setting it false did nothing
        // and the node kept contending for a distributed lease. An operator who had deliberately
        // taken a node out of the cluster still had it participating.
        if (!options.Value.Enabled)
        {
            election.Update(true, Timeout.InfiniteTimeSpan);
            logger.LogInformation("Cluster:Enabled is false — running standalone as permanent leader.");
            return;
        }

        var ttl = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseTtlSeconds));

        // The Azure blob-lease backend caps a lease at 60 seconds, so a configured TTL above that is
        // silently truncated by the backend while this node believes it holds the longer one — which
        // is sustained dual leadership, not a slow failover. Bounded here so the local deadline and
        // the backend agree.
        if (ttl > MaxLeaseTtl)
        {
            logger.LogWarning(
                "Cluster:LeaseTtlSeconds is {Configured}s; capping at {Max}s because lease backends do not " +
                "honour longer leases and the mismatch produces two nodes believing they are leader.",
                ttl.TotalSeconds, MaxLeaseTtl.TotalSeconds);
            ttl = MaxLeaseTtl;
        }

        var renewInterval = TimeSpan.FromSeconds(Math.Max(2, ttl.TotalSeconds / 2));

        using var timer = new PeriodicTimer(renewInterval);

        // Run the first attempt immediately so single-node leadership is established at startup.
        do
        {
            try
            {
                var held = await leaseProvider.TryAcquireOrRenewAsync(LeaderResource, node.NodeId, ttl, stoppingToken)
                    .ConfigureAwait(false);
                election.Update(held, ttl);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // On any backend error, relinquish leadership locally to avoid two leaders.
                election.Update(false, TimeSpan.Zero);
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
