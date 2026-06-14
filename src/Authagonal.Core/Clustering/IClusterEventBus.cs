namespace Authagonal.Core.Clustering;

/// <summary>
/// Fan-out of small control messages (e.g. cache-invalidation) to every node in the cluster.
/// Replaces the old peer-addressed gossip + headless-DNS fan-out — publishers no longer discover
/// or address individual replicas. The in-process default delivers to local subscribers only
/// (single node); the Azure-storage backend appends to a table log that all nodes poll.
/// Delivery is at-least-once and unordered, so handlers must be idempotent.
/// </summary>
public interface IClusterEventBus
{
    /// <summary>Publish <paramref name="payload"/> on <paramref name="topic"/> to all nodes.</summary>
    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>Register a handler for <paramref name="topic"/>. Dispose the returned token to unsubscribe.</summary>
    IDisposable Subscribe(string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler);
}
