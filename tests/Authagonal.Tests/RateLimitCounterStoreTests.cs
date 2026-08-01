using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The atomicity of the rate-limit increment, against a real database (#351).
/// </summary>
/// <remarks>
/// This is where the durable limiter's correctness actually rests. <see cref="DurableRateLimiterTests"/>
/// covers the window arithmetic with a double, but no in-memory double can demonstrate a lost update: with
/// no I/O the continuations drain in order, so a read-then-write store passes those tests. A real
/// connection per call, with real round trips, is what makes concurrent increments genuinely interleave.
/// <para>
/// SQLite is the backend under test because it runs in-process with no container. The statement it
/// exercises — <c>INSERT … ON CONFLICT … DO UPDATE SET version = version + 1 … RETURNING version</c> — is
/// the same one Postgres runs, and the Azure and DynamoDB implementations are held to the same contract by
/// their own provider suites. The property being pinned is the contract on
/// <see cref="IRateLimitCounterStore.IncrementAsync"/>: N increments yield exactly the values 1..N, with
/// none repeated and none skipped.
/// </para>
/// </remarks>
public sealed class RateLimitCounterStoreTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    private async Task<IRateLimitCounterStore> StoreAsync(string table)
    {
        await _source.EnsureTableAsync(table);
        return new SqlRateLimitCounterStore(new SqlTable(_source, table), EnvPartitioner.Live);
    }

    [Fact]
    public async Task AFirstIncrementCreatesTheBucketAtOne()
    {
        var store = await StoreAsync("RateLimitFirst");

        Assert.Equal(1, await store.IncrementAsync("k", DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public async Task SequentialIncrementsCount()
    {
        var store = await StoreAsync("RateLimitSeq");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);

        for (long expected = 1; expected <= 5; expected++)
            Assert.Equal(expected, await store.IncrementAsync("k", expiry));
    }

    [Fact]
    public async Task DistinctBucketsCountSeparately()
    {
        var store = await StoreAsync("RateLimitDistinct");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);

        Assert.Equal(1, await store.IncrementAsync("a", expiry));
        Assert.Equal(1, await store.IncrementAsync("b", expiry));
        Assert.Equal(2, await store.IncrementAsync("a", expiry));
    }

    /// <summary>
    /// Concurrent increments of one bucket return every value exactly once — no duplicates, no gaps.
    /// </summary>
    /// <remarks>
    /// A duplicate is a lost update, and a lost update in a rate limiter means the budget silently grew:
    /// two callers both told "you are number 7" are both under a budget of 10 when one of them should have
    /// been number 8. Non-vacuity proven by replacing the single statement with a SELECT followed by an
    /// UPDATE — this test fails with duplicate values, and the sequential tests above still pass.
    /// <para>
    /// This assertion, not <see cref="TheLimiterOverARealStoreSpendsOneBudget"/>, is the load-bearing one:
    /// SQLite serialises the store's round trips, so the budget can still come out right against a broken
    /// increment (it did, under the break above). Asserting on the exact set of returned counts is what
    /// detects the lost update regardless of how the driver happens to schedule. The same caveat is
    /// recorded on the MFA single-use claim tests, for the same reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentIncrementsNeitherLoseNorRepeatAValue()
    {
        var store = await StoreAsync("RateLimitConcurrent");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        const int callers = 60;

        var counts = await Task.WhenAll(
            Enumerable.Range(0, callers).Select(_ => Task.Run(() => store.IncrementAsync("hot", expiry))));

        Assert.Equal(Enumerable.Range(1, callers).Select(i => (long)i), counts.OrderBy(c => c));
    }

    /// <summary>
    /// The limiter over a real store: the budget is spent exactly once across concurrent callers.
    /// </summary>
    [Fact]
    public async Task TheLimiterOverARealStoreSpendsOneBudget()
    {
        var store = await StoreAsync("RateLimitLimiter");
        var limiter = new DurableRateLimiter(store, NullLogger<DurableRateLimiter>.Instance);
        const int budget = 5;
        const int callers = 40;

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            Task.Run(() => limiter.IsRateLimitedAsync("device|ABCD-1234", budget, TimeSpan.FromMinutes(1)))));

        Assert.Equal(budget, results.Count(limited => !limited));
    }
}
