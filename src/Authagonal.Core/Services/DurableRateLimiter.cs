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

    /// <summary>
    /// Floor on how long past its end a bucket is retained, whatever the window length.
    /// </summary>
    /// <remarks>
    /// Two windows is a generous margin for a one-minute budget and almost none for a short one. The poll
    /// limiters use a five-second window, so retention was bucket_end + 5s — and the sweep that collects
    /// these rows runs on the LEADER, whose clock is a different clock. A leader running more than five
    /// seconds ahead, which is ordinary drift for pods without tight NTP, deleted buckets that were still
    /// being incremented, and the budget restarted from zero with no error and no log line. The reset was
    /// silent by construction and its tolerance scaled with the window, so the same drift against a
    /// one-minute budget needed a minute of skew and against a five-second one needed five seconds.
    /// <para>
    /// A floor decouples the margin from the window. Retention costs one dead row for slightly longer, and
    /// the sweep exists to collect them.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longest a single counter round trip may take before it is treated as a store failure.
    /// </summary>
    /// <remarks>
    /// Deliberately short. This sits on the login and token hot paths, twice per login, and the value it
    /// protects is a bound that per-node limiting and the edge also enforce — so waiting is worth less than
    /// answering. Exceeding it takes the fail-open branch: the cluster-wide bound is unavailable for that
    /// call, which is the same situation as an unreachable store and gets the same answer.
    /// </remarks>
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Characters no backend will accept in a partition key, and a length every backend will.
    /// </summary>
    /// <remarks>
    /// Bucket keys are built from unvalidated caller input at anonymous call sites and reached the backend
    /// verbatim as a partition key: <c>saml-acs|{connectionId}|{peer}</c> takes a route value, and
    /// <c>login|id|{email}</c> and <c>register|to|{recipient}</c> both run before any format validation.
    /// Azure Table forbids <c>/ \ # ?</c> and control characters in a PartitionKey and caps it at 1024;
    /// DynamoDB caps a partition key at 2048 bytes. So <c>POST /saml/a%23b/acs</c> produced a key the store
    /// rejected with 400 InvalidInput on every attempt, forever — and a permanent, deterministic rejection
    /// took the fail-open branch, logging at Error each time. An attacker-driven flood of store errors and
    /// Error-level log lines, and proof that the fail-open needed no outage to reach.
    /// <para>
    /// A sanitised key keeps a short hash of the original appended whenever sanitising CHANGED anything, so
    /// two different sources cannot be folded into one budget by the substitution itself — <c>a#b</c> and
    /// <c>a_b</c> would otherwise collide and share a bucket.
    /// </para>
    /// </remarks>
    private static string StorageSafe(string bucketKey)
    {
        const int maxLength = 512;
        var needsRewrite = bucketKey.Length > maxLength;

        var sanitised = new System.Text.StringBuilder(Math.Min(bucketKey.Length, maxLength));
        foreach (var c in bucketKey)
        {
            var safe = c is not ('/' or '\\' or '#' or '?') && !char.IsControl(c);
            if (!safe) needsRewrite = true;
            if (sanitised.Length < maxLength)
                sanitised.Append(safe ? c : '_');
        }

        if (!needsRewrite) return bucketKey;

        // Derived from the ORIGINAL, so the disambiguator survives truncation as well as substitution.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bucketKey)))[..16];
        return $"{sanitised}~{hash}";
    }

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
        var bucketKey = StorageSafe($"{key}|{window.Ticks}|{bucket}");
        var bucketEnd = new DateTimeOffset((bucket + 1) * windowTicks, TimeSpan.Zero);
        var retention = TimeSpan.FromTicks(Math.Max((RetainedWindows - 1) * windowTicks, MinimumRetention.Ticks));
        var expiresAt = bucketEnd + retention;

        // Bounded, because the documented posture — a store failure must not take authentication down with
        // it — only ever covered a store that THROWS. A store that is merely slow converted straight into a
        // login outage: the login handler makes two of these checks on the hot path, each a synchronous round
        // trip, so multi-second store latency (an Azure hot-partition slowdown, a connection-pool stall, a
        // Dynamo throttle with SDK-internal retries) blocked every login for twice that, and the fail-open
        // below never fired because nothing had failed. A timeout is what turns "slow" into "failed" so the
        // posture applies to it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StoreTimeout);

        long count;
        try
        {
            count = await store.IncrementAsync(bucketKey, expiresAt, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER went away. Distinguished from our own timeout by whose token fired: the linked
            // source cancels for either reason, so testing `ct` rather than the linked token is what keeps a
            // timeout on the fail-open path instead of surfacing as a cancelled request.
            throw;
        }
        catch (RateLimitContentionException ex)
        {
            // Fails CLOSED, and this is the one case that does.
            //
            // Losing every compare-and-set on one counter does not mean the store is unwell; it means many
            // callers are hitting THIS budget at this instant, which is the condition being measured. The
            // backend used to report it as an ordinary store failure and land in the branch below, so the
            // guarantee inverted under load: more concurrency meant more contention meant the bound was
            // lifted rather than enforced, and an attacker with enough parallel connections against a device
            // user_code or a client secret had the excess waved through un-counted.
            //
            // Warning rather than Error: refusing the request is the limiter working, not failing. The count
            // is unknown, so this is not recorded against the budget — the caller is simply told to slow
            // down, which is also what relieves the contention.
            logger.LogWarning(ex,
                "Durable rate limiter could not settle a counter under contention; refusing the request. " +
                "Many callers are competing for one budget, which is what this limit exists to bound.");
            return true;
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
