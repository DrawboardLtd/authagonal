using System.Collections.Concurrent;

namespace Authagonal.Core.Clustering;

/// <summary>
/// Single-process event bus: publishes deliver synchronously to local subscribers. The default
/// when no clustering backend is configured — correct for single-node deployments where there are
/// no other replicas to notify. A faulting handler does not prevent delivery to the others.
/// </summary>
public sealed class InProcessClusterEventBus : IClusterEventBus
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Subscription, byte>> _topics = new();

    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!_topics.TryGetValue(topic, out var subs)) return;
        foreach (var sub in subs.Keys)
        {
            try { await sub.Handler(payload, ct).ConfigureAwait(false); }
            catch { /* handlers are idempotent and independent; isolate failures */ }
        }
    }

    public IDisposable Subscribe(string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        var subs = _topics.GetOrAdd(topic, _ => new ConcurrentDictionary<Subscription, byte>());
        var sub = new Subscription(this, topic, handler);
        subs[sub] = 0;
        return sub;
    }

    private void Unsubscribe(Subscription sub)
    {
        if (_topics.TryGetValue(sub.Topic, out var subs))
            subs.TryRemove(sub, out _);
    }

    private sealed class Subscription(
        InProcessClusterEventBus bus, string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        : IDisposable
    {
        public string Topic => topic;
        public Func<ReadOnlyMemory<byte>, CancellationToken, Task> Handler => handler;
        public void Dispose() => bus.Unsubscribe(this);
    }
}
