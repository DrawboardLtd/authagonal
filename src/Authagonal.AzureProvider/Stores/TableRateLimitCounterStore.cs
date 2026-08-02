using Authagonal.Core.Services;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Stores;

/// <summary>
/// <see cref="IRateLimitCounterStore"/> over Azure Table Storage, where the increment is an ETag
/// compare-and-set retried under contention.
/// </summary>
/// <remarks>
/// Table Storage has no server-side arithmetic, so this is the one backend that cannot express "add one"
/// in a single round trip. The shape is therefore read → increment → conditional write, looping while the
/// ETag says someone else got there first. That is NOT the read-then-write hazard the interface warns
/// about: the write is conditional on the exact row the increment was computed from, so a lost update is
/// impossible — a loser is told it lost and recomputes rather than overwriting.
/// <para>
/// The retry budget matters because a rate-limit bucket is the most contended row in the system by
/// design: every request against one budget targets one row. Two things follow, and the first version of
/// this class got both wrong.
/// </para>
/// <para>
/// Retries are spaced with jittered backoff. A tight loop of unsynchronised compare-and-set attempts
/// against one row does not converge — the losers all re-read and collide again on the next tick — so
/// under real contention the budget was consumed by callers stepping on each other rather than by callers
/// making progress. Azure Table also throttles a hot single-entity partition with 429/503, and neither
/// status was retried at all: the first throttled response propagated straight out.
/// </para>
/// <para>
/// And exhausting the budget now throws <see cref="RateLimitContentionException"/>, which
/// <see cref="DurableRateLimiter"/> fails CLOSED on. It previously threw a plain store exception, which
/// that limiter read as "the store is unreachable" and answered by allowing the request. Contention on a
/// rate-limit row is not an outage — it is many callers hitting one budget at once, which is the condition
/// this whole mechanism exists to bound — so allowing the request meant raising attack concurrency removed
/// the cluster-wide bound instead of tripping it. An attacker grinding a device <c>user_code</c> or a
/// client secret with enough parallelism got the excess waved through UN-COUNTED.
/// </para>
/// <para>
/// Table Storage also has no TTL, so nothing here reclaims a bucket. Collection is
/// <c>RateLimitCounterSweepService</c>'s job — without it this table grows by one row per distinct key
/// per window forever, which is the same unbounded-growth defect the in-process limiter's own class doc
/// describes, moved into storage.
/// </para>
/// </remarks>
public sealed class TableRateLimitCounterStore(
    TableClient rateLimitCountersTable, EnvPartitioner partitioner) : IRateLimitCounterStore
{
    /// <summary>Attempts before giving up on a contended bucket.</summary>
    private const int MaxAttempts = 25;

    /// <summary>First backoff ceiling, in milliseconds. Doubles per attempt up to <see cref="MaxBackoffMs"/>.</summary>
    private const int BaseBackoffMs = 2;

    /// <summary>
    /// Ceiling on one backoff. Bounds the worst case: four doublings then a flat cap over the remaining
    /// attempts is a few hundred milliseconds of waiting, and only for a caller that keeps losing races on
    /// one budget. Slowing that caller down is the correct behaviour for a rate limiter, not a cost.
    /// </summary>
    private const int MaxBackoffMs = 20;

    private const string CounterRowKey = "counter";

    /// <summary>
    /// Statuses worth another attempt rather than propagating: Azure throttling a hot partition, and the
    /// transient server-side classes. A rate-limit bucket is the hottest single entity in the system, so
    /// 429 here is an expected steady state under load and not an error.
    /// </summary>
    private static bool IsTransient(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>
    /// Full-jitter backoff. The jitter is the load-bearing part: equal delays would keep the same set of
    /// losers colliding on the same tick, which is how a generous retry budget gets spent making no
    /// progress.
    /// </summary>
    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ceiling = Math.Min(BaseBackoffMs << Math.Min(attempt, 4), MaxBackoffMs);
        return Task.Delay(Random.Shared.Next(1, ceiling + 1), ct);
    }

    public async Task<long> IncrementAsync(
        string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var partitionKey = partitioner.PK(bucketKey);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
                await BackoffAsync(attempt, ct).ConfigureAwait(false);

            TableEntity? existing = null;
            try
            {
                var response = await rateLimitCountersTable.GetEntityAsync<TableEntity>(
                    partitionKey, CounterRowKey, cancellationToken: ct).ConfigureAwait(false);
                existing = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // First hit in this window.
            }
            catch (RequestFailedException ex) when (IsTransient(ex.Status))
            {
                // Throttled or a transient server error. Previously propagated, and the limiter read that
                // as an outage and allowed the request — on the read side of the hottest row in the system.
                continue;
            }

            if (existing is null)
            {
                var created = new TableEntity(partitionKey, CounterRowKey)
                {
                    ["Count"] = 1L,
                    ["ExpiresAt"] = expiresAt,
                };

                try
                {
                    await rateLimitCountersTable.AddEntityAsync(created, ct).ConfigureAwait(false);
                    return 1;
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    // Another caller created it between the read and the insert; re-read and add to it.
                    continue;
                }
                catch (RequestFailedException ex) when (IsTransient(ex.Status))
                {
                    continue;
                }
            }

            var count = existing.GetInt64("Count") ?? 0;
            existing["Count"] = count + 1;
            existing["ExpiresAt"] = expiresAt;

            try
            {
                await rateLimitCountersTable.UpdateEntityAsync(
                    existing, existing.ETag, TableUpdateMode.Replace, ct).ConfigureAwait(false);
                return count + 1;
            }
            catch (RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                // 412: someone else incremented first. 404: the sweep collected the row underneath us.
                // Either way the value this attempt computed is stale, so recompute rather than write it.
                continue;
            }
            catch (RequestFailedException ex) when (IsTransient(ex.Status))
            {
                continue;
            }
        }

        // Contention, not an outage — see RateLimitContentionException. The limiter fails CLOSED on this,
        // which is the whole reason it is a distinct type: throwing a plain store exception here made
        // heavy concurrency on one budget REMOVE the bound instead of enforcing it.
        throw new RateLimitContentionException(
            $"Could not increment rate-limit counter '{bucketKey}' after {MaxAttempts} attempts: "
            + "too many concurrent callers on the same budget.");
    }
}
