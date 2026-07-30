namespace Authagonal.Core.Services;

/// <summary>
/// In-process sliding-window rate limiter. Per-node only — the authoritative global limit is
/// enforced at the edge (e.g. a WAF rule). This replaces the former gossip-synced distributed
/// limiter; with the cluster fan-out moved to a shared backplane there is no longer a cross-node
/// counter to maintain, and per-node limiting remains a cheap defence-in-depth backstop.
/// </summary>
/// <remarks>
/// The limiter that protects everything else must not itself be the cheapest thing to attack. Three
/// properties are load-bearing here, and none of them held before:
///
/// <list type="number">
/// <item>
/// <b>Bounded memory.</b> Keys embed attacker-chosen values (source IPs, emails, user codes), so an
/// unauthenticated caller mints new keys at will. Growth was bounded only by a prune that could free
/// nothing (below), so the dictionary grew without limit. There is now a hard cap with oldest-first
/// eviction, which is a real bound rather than a hoped-for one.
/// </item>
/// <item>
/// <b>The sweep must actually free entries.</b> The trigger was a COUNT (<c>&gt; 10_000</c>) but the
/// predicate was an AGE (<c>Start &lt; now - 2h</c>). Windows live for seconds to minutes, and
/// <c>Start</c> was reset on every hit, so a caller who created ten thousand keys in a few seconds hit the
/// threshold while nothing matched the predicate: the scan freed zero entries and then ran again on the
/// next insert, and the next. The sweep now keys off each entry's own expiry, so what it looks for is what
/// is actually collectable.
/// </item>
/// <item>
/// <b>No O(n) work under the global lock on the hot path.</b> The scan ran inside the lock every time a
/// new key arrived past the threshold — turning "add a key" into "walk ten thousand entries while every
/// other request on the node blocks". The sweep is now time-gated, so its cost is amortised across a
/// sweep interval instead of paid per insert.
/// </item>
/// </list>
/// </remarks>
public sealed class InProcessRateLimiter : IRateLimiter
{
    /// <summary>Default ceiling on tracked windows.</summary>
    public const int DefaultMaxTrackedWindows = 50_000;

    /// <summary>
    /// Hard ceiling on tracked windows. Reaching it evicts the oldest rather than growing: dropping a
    /// window means a caller gets a fresh budget, which is strictly better than exhausting node memory.
    /// Constructor-settable so a small node can lower it, and so the bound can be asserted in a test
    /// without inserting tens of thousands of keys.
    /// </summary>
    private readonly int MaxTrackedWindows;

    public InProcessRateLimiter(int maxTrackedWindows = DefaultMaxTrackedWindows)
    {
        if (maxTrackedWindows < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTrackedWindows));
        MaxTrackedWindows = maxTrackedWindows;
    }

    /// <summary>How often a full sweep may run, regardless of insert rate.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public Task<bool> IsRateLimitedAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (_windows.TryGetValue(key, out var w))
            {
                if (w.Start < now - window)
                {
                    w.Start = now;
                    w.Count = 1;
                }
                else
                {
                    w.Count++;
                }
                // Track the window length so the sweep knows when this entry becomes collectable. A key can
                // be used with different windows by different call sites; keep the longest.
                if (window > w.Length) w.Length = window;
            }
            else
            {
                MaintainCapacity(now);
                w = new Window { Start = now, Count = 1, Length = window };
                _windows[key] = w;
            }

            return Task.FromResult(w.Count > maxAttempts);
        }
    }

    /// <summary>
    /// Keeps the dictionary inside <see cref="MaxTrackedWindows"/>. Sweeps expired entries at most once per
    /// <see cref="SweepInterval"/>; if that is not enough, evicts oldest-first until there is room. Called
    /// with the lock held.
    /// </summary>
    private void MaintainCapacity(DateTimeOffset now)
    {
        if (_windows.Count < MaxTrackedWindows) return;

        if (now - _lastSweep >= SweepInterval)
        {
            _lastSweep = now;
            // Collectable = the window has elapsed, so the entry carries no live count.
            var expired = _windows
                .Where(kvp => kvp.Value.Start + kvp.Value.Length < now)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var k in expired)
                _windows.Remove(k);
        }

        if (_windows.Count < MaxTrackedWindows) return;

        // Still full: every tracked window is live, which means this is a flood of distinct keys rather
        // than accumulated garbage. Evict the oldest tenth so the cost is paid once per batch instead of
        // once per insert.
        var evictCount = Math.Max(1, MaxTrackedWindows / 10);
        var oldest = _windows
            .OrderBy(kvp => kvp.Value.Start)
            .Take(evictCount)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var k in oldest)
            _windows.Remove(k);
    }

    /// <summary>
    /// Number of windows currently tracked. Exposed so the memory bound can be asserted, and because an
    /// operator diagnosing limiter pressure needs it.
    /// </summary>
    public int TrackedWindows
    {
        get { lock (_lock) return _windows.Count; }
    }

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
        public TimeSpan Length;
    }
}
