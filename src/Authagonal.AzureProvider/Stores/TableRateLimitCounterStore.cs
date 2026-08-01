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
/// design: every request against one budget targets one row. It is generous, and exhausting it fails the
/// increment (the limiter then allows the request and logs) rather than blocking the caller.
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

    private const string CounterRowKey = "counter";

    public async Task<long> IncrementAsync(
        string bucketKey, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var partitionKey = partitioner.PK(bucketKey);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
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
        }

        throw new RequestFailedException(
            $"Could not increment rate-limit counter '{bucketKey}' after {MaxAttempts} attempts.");
    }
}
