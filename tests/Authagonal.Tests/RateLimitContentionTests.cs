using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Which way the cluster-wide limiter fails, and the backend that made it fail the wrong way.
/// </summary>
/// <remarks>
/// A durable limiter has two failure modes and they need opposite answers. A store it cannot reach is an
/// outage: refusing every request would turn a storage blip into a total authentication outage, so it allows
/// the call and logs at Error. Losing every compare-and-set on one counter is not an outage — a rate-limit
/// bucket is the most contended row in the system by design, so that is many callers hitting one budget at
/// this instant, which is the condition being measured.
/// <para>
/// Both used to arrive as the same exception, so both got the outage answer. The guarantee therefore
/// INVERTED under load: raising concurrency raised contention, which lifted the bound instead of enforcing
/// it, and an attacker grinding a device <c>user_code</c> or a client secret with enough parallel
/// connections had the excess waved through un-counted. The limiter was weakest exactly where it was needed.
/// </para>
/// </remarks>
public sealed class DurableRateLimiterFailureDirectionTests
{
    private sealed class ThrowingStore(Exception toThrow) : IRateLimitCounterStore
    {
        public Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
            => throw toThrow;
    }

    private static Task<bool> IsLimitedAsync(Exception storeFailure) =>
        new DurableRateLimiter(new ThrowingStore(storeFailure), NullLogger<DurableRateLimiter>.Instance)
            .IsRateLimitedAsync("k", maxAttempts: 5, window: TimeSpan.FromMinutes(1));

    /// <summary>Contention on one budget fails CLOSED — the caller is told to slow down.</summary>
    [Fact]
    public async Task ContentionOnOneBudget_RefusesTheRequest()
        => Assert.True(await IsLimitedAsync(new RateLimitContentionException("25 attempts, all lost")));

    /// <summary>
    /// A store that cannot be reached fails OPEN, which is the documented posture and stays.
    /// </summary>
    /// <remarks>
    /// The control that keeps the fix honest. "Refuse when anything goes wrong" would satisfy the assertion
    /// above and would mean a storage blip signs out the entire deployment — which is the outcome the
    /// fail-open was chosen to avoid, and the reason the two cases had to be told apart rather than merged.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableStore_AllowsTheRequest()
        => Assert.False(await IsLimitedAsync(new TimeoutException("no route to storage")));

    /// <summary>Cancellation is the caller going away, and propagates as itself.</summary>
    [Fact]
    public async Task ACancelledCall_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var limiter = new DurableRateLimiter(
            new ThrowingStore(new OperationCanceledException()), NullLogger<DurableRateLimiter>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            limiter.IsRateLimitedAsync("k", 5, TimeSpan.FromMinutes(1), cts.Token));
    }
}

/// <summary>
/// The Azure counter store under the contention it is guaranteed to meet — the one backend that cannot
/// express "add one" in a single round trip, and the one that had no tests at all.
/// </summary>
/// <remarks>
/// Table Storage has no server-side arithmetic, so the increment is read → increment → conditional write,
/// retried while the ETag says someone else won. DynamoDB's <c>ADD</c> and the SQL upsert settle in one
/// statement and can never contend; this is the only implementation where the retry budget, the backoff and
/// the throttling response are load-bearing, and it was the only one nothing exercised.
/// <para>
/// Against Azurite rather than a fake for the reason <c>RateLimitCounterStoreTests</c> gives: with no I/O
/// the continuations drain in order, so a read-then-write store passes every in-memory concurrency test.
/// Real round trips are what make the increments genuinely interleave.
/// </para>
/// </remarks>
[Collection("Azurite")]
public class AzureRateLimitCounterStoreTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableRateLimitCounterStore NewStore()
    {
        var table = _svc.GetTableClient($"ratelimit{Guid.NewGuid():N}"[..20]);
        table.CreateIfNotExists();
        return new TableRateLimitCounterStore(table, EnvPartitioner.Live);
    }

    private static DateTimeOffset Expiry => DateTimeOffset.UtcNow.AddMinutes(5);

    [Fact]
    public async Task AFirstIncrementCreatesTheBucketAtOne()
        => Assert.Equal(1, await NewStore().IncrementAsync("b", Expiry));

    [Fact]
    public async Task SequentialIncrementsCount()
    {
        var store = NewStore();

        Assert.Equal(1, await store.IncrementAsync("b", Expiry));
        Assert.Equal(2, await store.IncrementAsync("b", Expiry));
        Assert.Equal(3, await store.IncrementAsync("b", Expiry));
    }

    [Fact]
    public async Task DistinctBucketsDoNotShareACounter()
    {
        var store = NewStore();

        Assert.Equal(1, await store.IncrementAsync("one", Expiry));
        Assert.Equal(1, await store.IncrementAsync("two", Expiry));
    }

    /// <summary>
    /// The contract, under the concurrency this row is guaranteed to see: N increments yield exactly the
    /// values 1..N, none repeated and none skipped.
    /// </summary>
    /// <remarks>
    /// A repeated value is a lost update and would mean the limiter undercounts — failing open at exactly
    /// the traffic it exists to bound. The count is asserted as the exact SET of returned values rather
    /// than as a maximum, because a budget-level assertion passes against a broken compare-and-set.
    /// </remarks>
    [Fact]
    public async Task ConcurrentIncrementsYieldEveryValueExactlyOnce()
    {
        var store = NewStore();
        const int callers = 24;

        var results = await Task.WhenAll(
            Enumerable.Range(0, callers).Select(_ => store.IncrementAsync("hot", Expiry)));

        Assert.Equal(Enumerable.Range(1, callers).Select(i => (long)i), results.OrderBy(r => r));
    }

    /// <summary>
    /// Realistic contention resolves: every caller settles, nobody errors.
    /// </summary>
    /// <remarks>
    /// A regression guard rather than a proof of the backoff — measured, this passes with the backoff
    /// removed too, because Azurite is local and a few dozen callers converge inside the retry budget
    /// either way. Kept because it pins the behaviour that matters at ordinary load; the backoff's value is
    /// argued in the store's own remarks and demonstrated where it can be, in the test below.
    /// </remarks>
    [Fact]
    public async Task RealisticContentionOnOneBucketResolves()
    {
        var store = NewStore();
        const int callers = 40;

        var error = await Record.ExceptionAsync(() => Task.WhenAll(
            Enumerable.Range(0, callers).Select(_ => store.IncrementAsync("hottest", Expiry))));

        Assert.Null(error);
    }

    /// <summary>
    /// Contention past what any retry budget can serialise surfaces AS contention — not as an outage.
    /// </summary>
    /// <remarks>
    /// This is the whole fix in one assertion, and the number is deliberate: a single-row compare-and-set
    /// admits one winner per round trip, so hundreds of simultaneous callers cannot all settle inside 25
    /// attempts and no amount of backoff changes that. That is not a gap — refusing is correct. Hundreds of
    /// concurrent requests against ONE rate-limit budget is the abuse the limiter exists to bound, and
    /// <see cref="DurableRateLimiter"/> answers <c>RateLimitContentionException</c> by refusing the caller.
    /// <para>
    /// What was broken is the TYPE. This used to throw <c>RequestFailedException</c>, indistinguishable from
    /// the store being unreachable — so the limiter read the busiest possible moment as an outage and allowed
    /// the request. The bound was lifted by the load that made it necessary. So the assertion here is the
    /// exception's identity, because that identity is what decides which way the limiter fails.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExtremeContentionSurfacesAsContentionAndNotAsAnOutage()
    {
        var store = NewStore();
        const int callers = 250;

        var error = await Record.ExceptionAsync(() => Task.WhenAll(
            Enumerable.Range(0, callers).Select(_ => store.IncrementAsync("hottest", Expiry))));

        Assert.IsType<RateLimitContentionException>(error);
    }
}

/// <summary>
/// The Azure counter sweep, whose whole pass turns on a server-side OData filter.
/// </summary>
/// <remarks>
/// Two reasons this needs a real table. The filter is a string the service parses, so a predicate this code
/// cannot render throws at query time — on the leader, inside a catch that logs and waits for the next tick,
/// which is to say invisibly; only a real query proves it parses. And the pass previously abandoned itself on
/// the first per-row error other than 404/412, including the 429 that the very flood being swept produces, so
/// the condition under which retention matters most was the one that stopped it.
/// </remarks>
[Collection("Azurite")]
public class RateLimitCounterSweepTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private sealed class AlwaysLeader : Authagonal.Core.Clustering.ILeaderElection
    {
        public bool IsLeader => true;
        public string NodeId => "test-node";
        public string? LeaderId => "test-node";
    }

    [Fact]
    public async Task TheSweepCollectsExpiredRowsAndKeepsLiveOnes()
    {
        var table = _svc.GetTableClient($"sweep{Guid.NewGuid():N}"[..20]);
        table.CreateIfNotExists();

        var now = DateTimeOffset.UtcNow;
        await table.AddEntityAsync(new TableEntity("dead-1", "counter")
        {
            ["Count"] = 5L, ["ExpiresAt"] = now.AddMinutes(-10),
        });
        await table.AddEntityAsync(new TableEntity("dead-2", "counter")
        {
            ["Count"] = 1L, ["ExpiresAt"] = now.AddSeconds(-1),
        });
        await table.AddEntityAsync(new TableEntity("live", "counter")
        {
            ["Count"] = 3L, ["ExpiresAt"] = now.AddMinutes(10),
        });

        var sweep = new Authagonal.Server.Services.RateLimitCounterSweepService(
            table, new AlwaysLeader(),
            NullLogger<Authagonal.Server.Services.RateLimitCounterSweepService>.Instance);

        var (removed, failures) = await sweep.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal(0, failures);

        // The live row is still there, and it is the only one.
        var survivors = new List<string>();
        await foreach (var e in table.QueryAsync<TableEntity>(cancellationToken: CancellationToken.None))
            survivors.Add(e.PartitionKey);

        Assert.Equal(["live"], survivors);
    }

    /// <summary>
    /// A pass over a table with nothing to collect is a no-op, not an error.
    /// </summary>
    /// <remarks>
    /// The non-vacuity control on the filter: a predicate that matched nothing would also report zero
    /// removed, so the test above has to be paired with one that shows zero is a real answer here.
    /// </remarks>
    [Fact]
    public async Task TheSweepRemovesNothingWhenEveryRowIsLive()
    {
        var table = _svc.GetTableClient($"sweep{Guid.NewGuid():N}"[..20]);
        table.CreateIfNotExists();

        await table.AddEntityAsync(new TableEntity("live", "counter")
        {
            ["Count"] = 1L, ["ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var sweep = new Authagonal.Server.Services.RateLimitCounterSweepService(
            table, new AlwaysLeader(),
            NullLogger<Authagonal.Server.Services.RateLimitCounterSweepService>.Instance);

        Assert.Equal((0, 0), await sweep.SweepOnceAsync(CancellationToken.None));
    }
}
