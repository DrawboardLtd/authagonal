namespace Authagonal.Core.Services;

/// <summary>
/// The one operation a cluster-wide rate limiter needs from storage: add one to a counter and say what
/// the total became.
/// </summary>
/// <remarks>
/// Deliberately smaller than <see cref="IRateLimiter"/>. All of the window arithmetic — which bucket a
/// moment falls in, how long a bucket stays interesting, whether a count is over budget — lives once in
/// <see cref="DurableRateLimiter"/>, and a backend supplies only the atomic increment. Four backends
/// implementing "a rate limiter" independently is four chances to disagree about when a window rolls;
/// four backends implementing "add one and tell me the total" is not.
/// <para>
/// <b>The increment MUST be atomic.</b> A read-then-write implementation undercounts under concurrent
/// load, which is the exact traffic a limiter exists to bound — it would fail open at precisely the
/// moment it matters and look correct in every single-threaded test. That rules out
/// <c>IDistributedCache</c>, which offers no compare-and-swap (see the same note on
/// <c>DistributedCacheBffSessionStore</c>), and it is why each provider implements this with its own
/// conditional-write primitive rather than through a shared cache abstraction.
/// </para>
/// </remarks>
public interface IRateLimitCounterStore
{
    /// <summary>
    /// Atomically adds one to the counter at <paramref name="bucketKey"/> and returns the value after the
    /// increment. A bucket that does not exist counts as zero, so the first caller gets 1.
    /// </summary>
    /// <param name="bucketKey">
    /// Identifies one counter. Already carries the window bucket, so it is only ever incremented and never
    /// reset — a new window is a new key.
    /// </param>
    /// <param name="expiresAt">
    /// When this bucket stops being interesting. Backends with native row expiry (DynamoDB TTL) set it;
    /// the others record it so a sweep can collect the row later. Never used to decide whether a count is
    /// live — that is the limiter's job, from the bucket it asked for.
    /// </param>
    /// <returns>The counter's value after adding one.</returns>
    Task<long> IncrementAsync(string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default);
}
