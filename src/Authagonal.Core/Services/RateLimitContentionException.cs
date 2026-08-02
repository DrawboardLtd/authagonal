namespace Authagonal.Core.Services;

/// <summary>
/// A counter could not be incremented because too many callers were incrementing the SAME counter at once,
/// not because the store was unreachable.
/// </summary>
/// <remarks>
/// The distinction decides which way a rate limiter fails, and getting it wrong inverts the control.
/// <para>
/// A store that cannot be reached is an outage: the cluster-wide bound is unavailable, and refusing every
/// request would turn a storage blip into a total authentication outage. That is the documented posture and
/// it stays — <see cref="DurableRateLimiter"/> logs at Error and allows the call, leaving the per-node
/// limiter and the edge as what remains.
/// </para>
/// <para>
/// Losing a compare-and-set race repeatedly on one counter is the opposite situation. A rate-limit bucket is
/// the most contended row in the system BY DESIGN — every request against one budget targets one row — so
/// exhausting a retry budget there does not mean storage is unwell. It means many callers are hammering one
/// budget at this instant, which is the exact condition the limiter exists to bound. Treating it as an
/// outage and allowing the request made the guarantee invert under load: raising attack concurrency raised
/// contention, which removed the bound instead of tripping it, so the limiter failed open at precisely the
/// intensity that made it necessary. Signalled separately so it can fail CLOSED.
/// </para>
/// <para>
/// Throwing rather than returning a sentinel count: every backend must either produce a real post-increment
/// value or say it could not, and a magic number would be silently mistaken for one by any caller that
/// forgot to check.
/// </para>
/// </remarks>
public sealed class RateLimitContentionException : Exception
{
    public RateLimitContentionException(string message) : base(message) { }

    public RateLimitContentionException(string message, Exception? innerException)
        : base(message, innerException) { }
}
