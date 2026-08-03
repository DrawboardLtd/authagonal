namespace Authagonal.Core.Clustering;

/// <summary>
/// Lease provider that never grants leadership, for a node that must receive cluster events but must
/// never run the leader-gated jobs.
/// </summary>
/// <remarks>
/// The bus-only backend helpers — <c>UseAzureStorageBus</c>, <c>UseAwsDynamoBus</c>, <c>UseSqlBus</c> — are
/// documented as being for exactly that node ("so they can't win the lease away from the node that actually
/// runs the leader-gated jobs"), and they left <see cref="InProcessLeaseProvider"/> in place, whose
/// <c>TryAcquireOrRenewAsync</c> unconditionally returns <see langword="true"/>. So a node wired the
/// documented way became leader on its first tick and stayed leader on every tick after — the precise
/// opposite of the guarantee in its own XML docs, and while a real cluster node was also holding the
/// distributed lease. Two leaders means two nodes minting signing keys and sweeping the same rows.
/// <para>
/// A never-granting provider rather than removing <c>LeaderElectionService</c>: the loop is harmless when it
/// can never win, the state stays observable (<c>IsLeader</c> is answered by the same code path as
/// everywhere else), and a node whose registration is later changed to a real lease backend starts
/// contending without needing the hosted service put back.
/// </para>
/// </remarks>
public sealed class NeverLeaseProvider : ILeaseProvider
{
    public Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
        => Task.CompletedTask;
}
