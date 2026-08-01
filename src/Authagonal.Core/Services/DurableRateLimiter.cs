using Microsoft.Extensions.Logging;

namespace Authagonal.Core.Services;

/// <summary>
/// <see cref="IRateLimiter"/> whose counters live in the deployment's own store, so one budget is shared
/// by every replica instead of each node keeping its own.
/// </summary>
/// <remarks>
/// <see cref="InProcessRateLimiter"/> is per-node by construction, so on N replicas every budget is
/// really N times what it says. For most limits that is an acceptable backstop — the authoritative bound
/// is at the edge. For the ones guarding a guessable secret it is not: a device <c>user_code</c> is a
/// short string from a small alphabet, and the attempt budget is the only thing between an attacker and a
/// code that grants a live session. A bound that scales with replica count is the wrong shape there.
///
/// <para>
/// <b>Fixed window, not sliding.</b> Time is cut into buckets of the window's length and the bucket index
/// goes in the key, so a window never has to be reset — a new window is simply a key nothing has written
/// to yet. That is what lets the whole thing rest on one atomic increment, which every backend can do
/// correctly; a sliding window needs either a row per hit or a read-modify-write, and the second one is
/// the undercounting-under-load bug this class exists to avoid. The cost is boundary burst: a caller who
/// times a run across a bucket edge can spend two budgets back to back, so treat a budget of N as "N per
/// window, up to 2N across a boundary". Sizes here have that headroom.
/// </para>
///
/// <para>
/// <b>Fails open, loudly.</b> A store that is unreachable must not take authentication down with it — the
/// limiter is a guard on the login path, not the login path itself, and returning "rate limited" on a
/// storage blip would lock every user out of a working system. So a failure logs an error and allows the
/// request. That is a real weakening under a store outage, and it is why this is a cluster-wide bound
/// layered on the edge's bound rather than a replacement for it.
/// </para>
/// </remarks>
public sealed class DurableRateLimiter(
    IRateLimitCounterStore store,
    ILogger<DurableRateLimiter> logger) : IRateLimiter
{
    /// <summary>
    /// How long past its end a bucket row is kept. One extra window, so a row is collectable well after
    /// the last request that could legitimately increment it, without keeping garbage around.
    /// </summary>
    private const int RetainedWindows = 2;

    public async Task<bool> IsRateLimitedAsync(
        string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "A rate-limit window must be positive.");

        var now = DateTimeOffset.UtcNow;
        var windowTicks = window.Ticks;

        // The bucket index, and with it the key, is derived from absolute time rather than from when this
        // key was first seen. Every replica therefore computes the same bucket for the same instant with
        // no coordination — which is the whole point, and is also why no node ever has to reset a counter.
        var bucket = now.UtcTicks / windowTicks;
        var bucketKey = $"{key}|{window.Ticks}|{bucket}";
        var expiresAt = new DateTimeOffset((bucket + RetainedWindows) * windowTicks, TimeSpan.Zero);

        long count;
        try
        {
            count = await store.IncrementAsync(bucketKey, expiresAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Named at Error, not Warning: a limiter that is silently not limiting is the kind of thing
            // that is discovered during the incident it failed to prevent.
            logger.LogError(ex,
                "Durable rate limiter could not reach its store; allowing the request. The cluster-wide " +
                "bound is NOT in effect for this call — per-node limits and the edge are what remain.");
            return false;
        }

        return count > maxAttempts;
    }
}
