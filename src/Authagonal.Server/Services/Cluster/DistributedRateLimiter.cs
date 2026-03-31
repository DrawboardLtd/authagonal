using Authagonal.Core.Services;

namespace Authagonal.Server.Services.Cluster;

public sealed class DistributedRateLimiter : IRateLimiter
{
    private readonly string _nodeId;
    private readonly object _lock = new();
    private readonly Dictionary<string, WindowCounter> _localCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerState> _peerStates = new(StringComparer.OrdinalIgnoreCase);

    public DistributedRateLimiter(string nodeId)
    {
        _nodeId = nodeId;
    }

    public string NodeId => _nodeId;

    public Task<bool> IsRateLimitedAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var windowStart = now - window;

            // Increment local counter
            if (_localCounters.TryGetValue(key, out var counter))
            {
                if (counter.WindowStart < windowStart)
                {
                    // Window expired — reset
                    counter.WindowStart = now;
                    counter.Count = 1;
                }
                else
                {
                    counter.Count++;
                }
            }
            else
            {
                _localCounters[key] = new WindowCounter { Count = 1, WindowStart = now };
            }

            // Sum across local + all peers
            var total = _localCounters.TryGetValue(key, out var local) ? local.Count : 0;

            foreach (var peer in _peerStates.Values)
            {
                if (peer.Counters.TryGetValue(key, out var peerCounter) && peerCounter.WindowStart >= windowStart)
                {
                    total += peerCounter.Count;
                }
            }

            return Task.FromResult(total > maxAttempts);
        }
    }

    public GossipMessage GetLocalState()
    {
        lock (_lock)
        {
            var snapshot = new Dictionary<string, WindowCounter>(_localCounters.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _localCounters)
            {
                snapshot[kvp.Key] = new WindowCounter { Count = kvp.Value.Count, WindowStart = kvp.Value.WindowStart };
            }
            return new GossipMessage { NodeId = _nodeId, Counters = snapshot };
        }
    }

    public void MergePeerState(string peerId, Dictionary<string, WindowCounter> counters)
    {
        if (string.Equals(peerId, _nodeId, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            if (!_peerStates.TryGetValue(peerId, out var existing))
            {
                _peerStates[peerId] = new PeerState
                {
                    Counters = new Dictionary<string, WindowCounter>(counters, StringComparer.OrdinalIgnoreCase),
                    LastUpdated = DateTimeOffset.UtcNow
                };
                return;
            }

            // Take max per key (idempotent CRDT merge)
            foreach (var kvp in counters)
            {
                if (existing.Counters.TryGetValue(kvp.Key, out var existingCounter))
                {
                    // If the incoming window is newer, replace entirely
                    if (kvp.Value.WindowStart > existingCounter.WindowStart)
                    {
                        existing.Counters[kvp.Key] = new WindowCounter
                        {
                            Count = kvp.Value.Count,
                            WindowStart = kvp.Value.WindowStart
                        };
                    }
                    // Same window — take the max count
                    else if (kvp.Value.WindowStart == existingCounter.WindowStart && kvp.Value.Count > existingCounter.Count)
                    {
                        existingCounter.Count = kvp.Value.Count;
                    }
                }
                else
                {
                    existing.Counters[kvp.Key] = new WindowCounter
                    {
                        Count = kvp.Value.Count,
                        WindowStart = kvp.Value.WindowStart
                    };
                }
            }

            existing.LastUpdated = DateTimeOffset.UtcNow;
        }
    }

    public void Prune(TimeSpan peerStaleAfter)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            // Remove expired local windows (older than 2 hours to be safe)
            var expiredKeys = _localCounters
                .Where(kvp => now - kvp.Value.WindowStart > TimeSpan.FromHours(2))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
                _localCounters.Remove(key);

            // Remove stale peers
            var stalePeers = _peerStates
                .Where(kvp => now - kvp.Value.LastUpdated > peerStaleAfter)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var peerId in stalePeers)
                _peerStates.Remove(peerId);

            // Remove expired windows from active peers
            foreach (var peer in _peerStates.Values)
            {
                var peerExpired = peer.Counters
                    .Where(kvp => now - kvp.Value.WindowStart > TimeSpan.FromHours(2))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in peerExpired)
                    peer.Counters.Remove(key);
            }
        }
    }

    private sealed class PeerState
    {
        public Dictionary<string, WindowCounter> Counters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LastUpdated { get; set; }
    }
}
