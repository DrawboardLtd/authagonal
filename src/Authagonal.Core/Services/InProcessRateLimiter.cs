namespace Authagonal.Core.Services;

/// <summary>
/// In-process sliding-window rate limiter. Per-node only — the authoritative global limit is
/// enforced at the edge (e.g. a WAF rule). This replaces the former gossip-synced distributed
/// limiter; with the cluster fan-out moved to a shared backplane there is no longer a cross-node
/// counter to maintain, and per-node limiting remains a cheap defence-in-depth backstop.
/// </summary>
public sealed class InProcessRateLimiter : IRateLimiter
{
    private static readonly TimeSpan PruneStaleAfter = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsRateLimitedAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (_windows.TryGetValue(key, out var w))
            {
                if (w.Start < now - window) { w.Start = now; w.Count = 1; }
                else w.Count++;
            }
            else
            {
                w = new Window { Start = now, Count = 1 };
                _windows[key] = w;

                // Bound memory: occasionally drop windows that can no longer be active.
                if (_windows.Count > 10_000)
                    Prune(now - PruneStaleAfter);
            }

            return Task.FromResult(w.Count > maxAttempts);
        }
    }

    private void Prune(DateTimeOffset cutoff)
    {
        var stale = _windows.Where(kvp => kvp.Value.Start < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in stale)
            _windows.Remove(key);
    }

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
    }
}
