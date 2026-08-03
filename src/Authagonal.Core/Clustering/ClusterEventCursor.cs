namespace Authagonal.Core.Clustering;

/// <summary>
/// Keeps a cluster event-bus consumer cursor from being pushed into the future by a publisher whose clock
/// runs fast.
/// </summary>
/// <remarks>
/// All three bus backends key each event row on <c>$"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid}"</c> taken
/// from the PUBLISHER's clock, and each consumer keeps a per-topic high-water cursor advanced to the largest
/// key it has seen, querying only rows greater than it. So if any node's clock runs ahead by Δ, its publishes
/// push every consumer's cursor Δ into the future, and every event published by a correctly-clocked node
/// during the next Δ of real time sorts BELOW that cursor and is delivered to nobody — silently, with no
/// error and no log line. The events in question are cache and session invalidations, so the observable
/// symptom is a revoked token still working on some nodes, which is not a symptom anyone traces back to NTP.
/// <para>
/// This codebase already treats multi-second skew as ordinary: <c>DurableRateLimiter.MinimumRetention</c>
/// calls five seconds of disagreement between replicas normal. Δ therefore need not be a broken clock, and
/// the damage is durable — the cursor is never reconciled against wall clock once set.
/// </para>
/// <para>
/// The rule here: a row dated in the future is DELIVERED (dropping it would be the same bug in the other
/// direction) but does not advance the cursor. So the cursor tracks real time, later events from other nodes
/// still sort above it, and the future row is re-read on subsequent polls until real time passes it — which
/// is why delivery has to be de-duplicated. Only future-dated keys are remembered, so in a healthy cluster
/// this holds nothing at all and behaves exactly as before.
/// </para>
/// </remarks>
public static class ClusterEventCursor
{
    /// <summary>
    /// The 19-digit tick prefix that row keys sort against, for comparing a row key to a point in time.
    /// </summary>
    /// <remarks>
    /// A row key is <c>{ticks:D19}-{guid}</c>, so ordinal-comparing one against a bare <c>{ticks:D19}</c>
    /// orders every row in that tick above the bound — which is the safe direction: a row stamped exactly
    /// now counts as future and merely defers advancing the cursor by one poll.
    /// </remarks>
    public static string TimeBound(DateTimeOffset at) => $"{at.UtcTicks:D19}";

    /// <summary>True when <paramref name="rowKey"/> is dated after <paramref name="nowBound"/>.</summary>
    public static bool IsAfter(string rowKey, string nowBound)
        => string.CompareOrdinal(rowKey, nowBound) > 0;
}

/// <summary>
/// Remembers which future-dated event rows have already been handed to subscribers, so re-reading them on
/// later polls does not deliver them twice.
/// </summary>
/// <remarks>
/// Bounded. Only future-dated keys are ever held — a key stops being future as real time advances, at which
/// point it moves the cursor and is forgotten — so a cluster with agreeing clocks keeps this empty. At the
/// cap the oldest entry is dropped, which can re-deliver one event: that requires more than
/// <see cref="Capacity"/> future-dated events outstanding at once, and cluster events are idempotent
/// invalidations, so a duplicate is a wasted cache eviction rather than a fault.
/// </remarks>
public sealed class ClusterEventDeduper
{
    /// <summary>How many outstanding future-dated keys to remember per topic.</summary>
    public const int Capacity = 4096;

    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();

    /// <summary>True when this row has not been delivered yet.</summary>
    public bool ShouldDeliver(string rowKey)
    {
        lock (_lock) return !_delivered.Contains(rowKey);
    }

    /// <summary>Records that a future-dated row has been delivered.</summary>
    public void RecordDelivered(string rowKey)
    {
        lock (_lock)
        {
            if (!_delivered.Add(rowKey)) return;
            _order.Enqueue(rowKey);

            while (_order.Count > Capacity && _order.TryDequeue(out var evicted))
                _delivered.Remove(evicted);
        }
    }

    /// <summary>
    /// Drops a row the cursor has now moved past, so it cannot be queried again and need not be remembered.
    /// </summary>
    public void Forget(string rowKey)
    {
        lock (_lock)
        {
            if (_delivered.Remove(rowKey))
            {
                // Leave the queue entry: it is a bounded eviction order, and removing from the middle of a
                // Queue is O(n). A stale entry only ever evicts a key that is already gone.
            }
        }
    }

    /// <summary>Outstanding future-dated rows, for diagnostics and tests.</summary>
    public int Count
    {
        get { lock (_lock) return _delivered.Count; }
    }
}
