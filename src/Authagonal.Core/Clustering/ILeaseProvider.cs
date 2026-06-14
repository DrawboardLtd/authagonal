namespace Authagonal.Core.Clustering;

/// <summary>
/// Backend seam for leader election: a renewable, single-holder lease over a named resource.
/// The in-process default always grants (single node); the Azure-storage backend uses a blob lease,
/// which Azure guarantees to at most one holder at a time.
/// </summary>
public interface ILeaseProvider
{
    /// <summary>
    /// Acquire the lease for <paramref name="resource"/> on behalf of <paramref name="nodeId"/>, or renew
    /// it if this node already holds it. Returns true if the lease is held after the call.
    /// </summary>
    Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Release the lease if held by this node. Best-effort; never throws.</summary>
    Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default);
}
