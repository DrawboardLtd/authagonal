using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The cluster-wide rate limiter (#351): one budget shared by every replica instead of one per node.
/// </summary>
/// <remarks>
/// What these pin is the arithmetic and the failure posture, because those are the parts that live in
/// Core and are shared by all four backends. The per-backend atomicity of the increment is a storage
/// property and is covered in the parity tests, against real Azurite / DynamoDB-Local / SQLite.
/// </remarks>
public class DurableRateLimiterTests
{
    private static DurableRateLimiter Limiter(IRateLimitCounterStore store) =>
        new(store, NullLogger<DurableRateLimiter>.Instance);

    /// <summary>
    /// The budget is the budget: N through, the next one refused. This is the property the whole thing
    /// exists for — with the in-process limiter this held per node, so on three replicas it was 3N.
    /// </summary>
    [Fact]
    public async Task TheBudgetIsSpentOnceAcrossEveryCaller()
    {
        var store = new InMemoryRateLimitCounterStore();
        var limiter = Limiter(store);

        for (var i = 0; i < 5; i++)
            Assert.False(await limiter.IsRateLimitedAsync("device|ABCD-1234", 5, TimeSpan.FromMinutes(1)));

        Assert.True(await limiter.IsRateLimitedAsync("device|ABCD-1234", 5, TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// Two limiter instances stand in for two replicas: separate objects, no shared memory, one store.
    /// Against <c>InProcessRateLimiter</c> this test fails by construction — each instance would carry
    /// its own count and both callers would stay under budget.
    /// </summary>
    [Fact]
    public async Task TwoReplicasShareOneBudget()
    {
        var store = new InMemoryRateLimitCounterStore();
        var nodeA = Limiter(store);
        var nodeB = Limiter(store);

        Assert.False(await nodeA.IsRateLimitedAsync("k", 2, TimeSpan.FromMinutes(1)));
        Assert.False(await nodeB.IsRateLimitedAsync("k", 2, TimeSpan.FromMinutes(1)));

        // The third attempt is over budget wherever it lands.
        Assert.True(await nodeA.IsRateLimitedAsync("k", 2, TimeSpan.FromMinutes(1)));
        Assert.True(await nodeB.IsRateLimitedAsync("k", 2, TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// Many callers against one key spend one budget: exactly the budget passes and every later call is
    /// refused, whatever order they arrive in.
    /// </summary>
    /// <remarks>
    /// This pins the limiter, NOT the atomicity of the increment. The limiter holds no per-call state — it
    /// makes one <c>IncrementAsync</c> call and compares the answer — so given an atomic store this result
    /// follows, and given a non-atomic one it is the store that is broken. An in-memory double cannot
    /// demonstrate the difference: with no real I/O the continuations drain in order and a deliberately
    /// read-then-write double still passes, which is worth stating because a test that looks like a
    /// concurrency proof and is not is worse than no test. The atomicity of each real increment is proven
    /// where it lives, against a real database, in <see cref="RateLimitCounterStoreTests"/>.
    /// </remarks>
    [Fact]
    public async Task TheBudgetIsExactRegardlessOfArrivalOrder()
    {
        var store = new InMemoryRateLimitCounterStore();
        var limiter = Limiter(store);
        const int budget = 10;
        const int callers = 500;

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            limiter.IsRateLimitedAsync("burst", budget, TimeSpan.FromMinutes(1))));

        Assert.Equal(budget, results.Count(limited => !limited));
        Assert.Equal(callers - budget, results.Count(limited => limited));
        // One store round trip per check, so the count is the attempt count and nothing is double-spent.
        Assert.Equal(callers, store.Keys.Count);
    }

    /// <summary>The verdict boundary: the call that reaches the budget passes, the next one does not.</summary>
    [Fact]
    public async Task TheCallThatReachesTheBudgetIsAllowedAndTheNextIsNot()
    {
        var store = new InMemoryRateLimitCounterStore();
        var limiter = Limiter(store);

        Assert.False(await limiter.IsRateLimitedAsync("k", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await limiter.IsRateLimitedAsync("k", 1, TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// Two windows of the same length are different buckets, and a different window length on the same
    /// logical key is a different bucket too — otherwise a 1-minute budget and a 1-hour budget on one key
    /// would spend each other's allowance.
    /// </summary>
    [Fact]
    public async Task TheBucketKeyCarriesBothTheWindowAndItsIndex()
    {
        var store = new InMemoryRateLimitCounterStore();
        var limiter = Limiter(store);

        await limiter.IsRateLimitedAsync("k", 5, TimeSpan.FromMinutes(1));
        await limiter.IsRateLimitedAsync("k", 5, TimeSpan.FromMinutes(5));

        var keys = store.Keys.ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith("k|", k, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every replica must derive the same bucket for the same instant with no coordination, which means
    /// the index comes from absolute time rather than from when a node first saw the key.
    /// </summary>
    [Fact]
    public async Task TheBucketIsDerivedFromAbsoluteTimeNotFirstUse()
    {
        var store = new InMemoryRateLimitCounterStore();
        var window = TimeSpan.FromMinutes(1);

        await Limiter(store).IsRateLimitedAsync("k", 5, window);
        await Task.Delay(20);
        await Limiter(store).IsRateLimitedAsync("k", 5, window);

        // Two calls milliseconds apart fall in one bucket, so a fresh instance does not start a fresh
        // window — it joins the one already in progress.
        Assert.Single(store.Keys.Distinct(StringComparer.Ordinal));
    }

    /// <summary>The retained expiry is past the end of the window it belongs to, so a live bucket is never swept.</summary>
    [Fact]
    public async Task ABucketOutlivesItsOwnWindow()
    {
        var store = new InMemoryRateLimitCounterStore();
        var window = TimeSpan.FromMinutes(1);

        await Limiter(store).IsRateLimitedAsync("k", 5, window);

        var expiry = Assert.Single(store.Expiries).Value;
        Assert.True(expiry > DateTimeOffset.UtcNow + window,
            $"a bucket must stay collectable-free for at least its own window; expiry was {expiry:o}");
    }

    /// <summary>
    /// A store outage allows the request rather than refusing it. The limiter guards the login path; it is
    /// not the login path, and returning "limited" on a storage blip would lock every user out of a system
    /// that is otherwise working.
    /// </summary>
    [Fact]
    public async Task AStoreOutageFailsOpen()
    {
        var store = new InMemoryRateLimitCounterStore { Fail = true };

        Assert.False(await Limiter(store).IsRateLimitedAsync("k", 1, TimeSpan.FromMinutes(1)));
    }

    /// <summary>Cancellation is the caller going away, not a store failure, so it must not be swallowed as one.</summary>
    [Fact]
    public async Task CancellationPropagates()
    {
        var store = new CancellingCounterStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Limiter(store).IsRateLimitedAsync("k", 1, TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public async Task AZeroWindowIsRefusedRatherThanDividingByIt()
    {
        var store = new InMemoryRateLimitCounterStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Limiter(store).IsRateLimitedAsync("k", 1, TimeSpan.Zero));
    }

    private sealed class CancellingCounterStore : IRateLimitCounterStore
    {
        public Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(1L);
        }
    }

    /// <summary>
    /// The DynamoDB increment must write the SAME attribute the provisioner enables TTL on.
    /// </summary>
    /// <remarks>
    /// It wrote <c>_ttl</c>; <c>DynamoTableProvisioner</c> enabled TTL on <c>ttl</c>. So nothing ever
    /// expired, on the one backend with no sweeper for these rows — Azure has
    /// <c>RateLimitCounterSweepService</c> and SQL has <c>SqlExpiryReaper</c>, and neither covers AWS.
    /// Enabling the documented multi-replica limiter turned unauthenticated request volume into unbounded
    /// permanent storage: one item per source address for login/registration/DCR, per target address for
    /// forgot-password, per <c>user_code</c> for the device flow.
    /// <para>
    /// A source check because the two names live in different assemblies and nothing else compares them —
    /// the increment now references the provisioner's constant, and this is what keeps it doing so. There is
    /// no assertion available against a real table: DynamoDB Local implements TTL as a no-op.
    /// </para>
    /// </remarks>
    [Fact]
    public void DynamoRateLimitIncrementWritesTheProvisionedTtlAttribute()
    {
        var path = Path.Combine(RepositoryRoot(),
            "src/Authagonal.AwsProvider/Dynamo/DynamoTable.cs".Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"expected {path}");

        var text = File.ReadAllText(path);

        Assert.Contains("DynamoTableProvisioner.TtlAttribute", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"_ttl\"", text, StringComparison.Ordinal);
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
