using Authagonal.Core.Clustering;

namespace Authagonal.Tests;

/// <summary>
/// A publisher whose clock runs fast must not make other nodes' events disappear.
/// </summary>
/// <remarks>
/// All three bus backends key each event row on <c>$"{UtcNow.UtcTicks:D19}-{Guid}"</c> from the PUBLISHER's
/// clock, and each consumer keeps a per-topic high-water cursor advanced to the largest key seen, querying
/// only rows above it. So a node running ahead by Δ pushed every consumer's cursor Δ into the future, and
/// every event published by a correctly-clocked node during the next Δ of real time sorted BELOW that cursor
/// and reached nobody — no error, no log line, and never reconciled afterwards.
/// <para>
/// The events are cache and session invalidations, so the symptom is a revoked token still working on some
/// nodes. Nobody traces that back to NTP. And this codebase already treats multi-second skew as ordinary:
/// <c>DurableRateLimiter.MinimumRetention</c> calls five seconds of replica disagreement normal.
/// </para>
/// </remarks>
public sealed class ClusterEventCursorTests
{
    private static string Key(DateTimeOffset at) => $"{at.UtcTicks:D19}-{Guid.NewGuid():N}";

    /// <summary>A row from a fast clock is recognised as future; one from a correct clock is not.</summary>
    [Fact]
    public void FutureRowsAreDistinguishedFromCurrentOnes()
    {
        var now = DateTimeOffset.UtcNow;
        var bound = ClusterEventCursor.TimeBound(now);

        Assert.True(ClusterEventCursor.IsAfter(Key(now.AddMinutes(1)), bound));
        Assert.False(ClusterEventCursor.IsAfter(Key(now.AddMinutes(-1)), bound));
    }

    /// <summary>
    /// The whole point: an event published after a future-dated one still sorts above the cursor.
    /// </summary>
    /// <remarks>
    /// This is the property the old code violated. Advancing the cursor to the fast node's key put it ahead
    /// of real time, so the next event from any correctly-clocked node compared BELOW it and the
    /// <c>RowKey gt cursor</c> query never returned it.
    /// </remarks>
    [Fact]
    public void HoldingTheCursorAtRealTimeKeepsLaterEventsVisible()
    {
        var now = DateTimeOffset.UtcNow;
        var bound = ClusterEventCursor.TimeBound(now);

        // A node one minute fast publishes; then a correctly-clocked node publishes a moment later.
        var fromFastNode = Key(now.AddMinutes(1));
        var fromCorrectNode = Key(now.AddSeconds(1));

        // Cursor advanced only by non-future rows: the fast node's key does not move it.
        var cursorHeldAtRealTime = ClusterEventCursor.IsAfter(fromFastNode, bound) ? bound : fromFastNode;

        Assert.True(string.CompareOrdinal(fromCorrectNode, cursorHeldAtRealTime) > 0,
            "an event published after a future-dated one must still sort above the cursor");

        // And the defect, stated: had the cursor taken the fast node's key, it would not have.
        Assert.False(string.CompareOrdinal(fromCorrectNode, fromFastNode) > 0);
    }

    /// <summary>A future row is delivered once, however many polls re-read it.</summary>
    /// <remarks>
    /// Holding the cursor back means the row is queried again on every poll until real time passes it, so
    /// without de-duplication the fix would trade lost events for repeated ones.
    /// </remarks>
    [Fact]
    public void AFutureRowIsDeliveredOnlyOnce()
    {
        var deduper = new ClusterEventDeduper();
        var key = Key(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(deduper.ShouldDeliver(key));
        deduper.RecordDelivered(key);

        Assert.False(deduper.ShouldDeliver(key));
        Assert.False(deduper.ShouldDeliver(key));
    }

    /// <summary>Once the cursor passes a row, it is forgotten — so a healthy cluster remembers nothing.</summary>
    [Fact]
    public void RowsThePastHasCaughtUpWithAreForgotten()
    {
        var deduper = new ClusterEventDeduper();
        var key = Key(DateTimeOffset.UtcNow.AddSeconds(2));

        deduper.RecordDelivered(key);
        Assert.Equal(1, deduper.Count);

        // Real time has passed it: the cursor moves past, so it can never be re-read.
        deduper.Forget(key);

        Assert.Equal(0, deduper.Count);
        Assert.True(deduper.ShouldDeliver(key));
    }

    /// <summary>Memory is bounded even if a clock is wildly wrong.</summary>
    /// <remarks>
    /// At the cap the oldest entry is dropped, which can re-deliver one event. Cluster events are idempotent
    /// invalidations, so a duplicate is a wasted cache eviction — the alternative, unbounded growth driven by
    /// a remote node's clock, is not a trade worth making.
    /// </remarks>
    [Fact]
    public void TheDeduperIsBounded()
    {
        var deduper = new ClusterEventDeduper();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        for (var i = 0; i < ClusterEventDeduper.Capacity + 500; i++)
            deduper.RecordDelivered(Key(future));

        Assert.InRange(deduper.Count, 1, ClusterEventDeduper.Capacity);
    }

    /// <summary>All three backends hold the cursor back rather than one of them.</summary>
    /// <remarks>
    /// A source check: exercising the three drain loops needs live Azure Table, DynamoDB and SQL. The defect
    /// was identical in all three — same row-key format, same high-water cursor — so it would come back the
    /// same way in whichever one a future change misses.
    /// </remarks>
    [Fact]
    public void EveryBusBackendHoldsTheCursorAtRealTime()
    {
        string[] buses =
        [
            "src/Authagonal.AzureProvider/Clustering/TableClusterEventBus.cs",
            "src/Authagonal.SqlProvider/Clustering/SqlClusterEventBus.cs",
            "src/Authagonal.AwsProvider/Clustering/DynamoClusterEventBus.cs",
        ];

        foreach (var bus in buses)
        {
            var path = Path.Combine(RepositoryRoot(), bus.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"expected {path}");

            var text = File.ReadAllText(path);
            Assert.Contains("ClusterEventCursor.IsAfter", text, StringComparison.Ordinal);
            Assert.Contains("Dedupe.ShouldDeliver", text, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
