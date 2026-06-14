namespace Authagonal.Core.Clustering;

/// <summary>
/// Single-node lease provider: always grants leadership. The default when no clustering backend
/// is configured — correct for single-node deployments and local development.
/// </summary>
public sealed class InProcessLeaseProvider : ILeaseProvider
{
    public Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
        => Task.CompletedTask;
}
