using Authagonal.Core.Clustering;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Server.Services;

/// <summary>
/// Deletes expired rate-limit bucket rows on a timer, on the leader only. Azure Table path only.
/// </summary>
/// <remarks>
/// The durable limiter cuts time into fixed windows and puts the window index in the row key, so a bucket
/// is never reused once its window passes — which is what makes the increment a single atomic operation,
/// and also means every distinct key leaves one dead row per window behind it. Keys embed
/// attacker-chosen values (source IPs, emails, user codes, client ids), so on an anonymous endpoint that
/// is one permanent row per attempt: unbounded growth driven by exactly the traffic the limiter exists to
/// bound, which would make turning cluster-wide limiting on a storage-exhaustion primitive.
/// <para>
/// DynamoDB expires buckets through its native TTL attribute and SQL rows are collected by
/// <c>SqlExpiryReaper</c>, so both are covered by the store they already run. Table Storage has neither,
/// which is why this exists — the same gap, and the same fix, as
/// <see cref="Oidc.OidcStateSweepService"/> for federation state.
/// </para>
/// <para>
/// Correctness never depends on the sweep. A bucket past its window is simply never addressed again: the
/// limiter derives the key from the current time, so a stale row cannot be read, counted, or reset into.
/// This is retention only.
/// </para>
/// </remarks>
internal sealed class RateLimitCounterSweepService(
    [FromKeyedServices("RateLimitCounters")] TableClient table,
    ILeaderElection election,
    ILogger<RateLimitCounterSweepService> logger) : BackgroundService
{
    /// <summary>
    /// Frequent relative to the other sweeps, because these rows are created far faster than federation
    /// state and every one of them is dead within two windows.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            // Leader only: every node scanning the same table and racing each other's deletes buys
            // nothing, and this is the table under the most write pressure.
            if (!election.IsLeader) continue;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var removed = 0;

                await foreach (var entity in table
                    .QueryAsync<TableEntity>(e => e.RowKey == "counter", cancellationToken: stoppingToken)
                    .ConfigureAwait(false))
                {
                    // The store writes ExpiresAt as the end of the retained window, so this is the row's
                    // own statement of when it stopped mattering. A row without one is from no writer
                    // this code has, and is swept rather than kept forever for lack of a field.
                    if (entity.GetDateTimeOffset("ExpiresAt") is { } expiresAt && expiresAt > now) continue;

                    try
                    {
                        await table.DeleteEntityAsync(
                            entity.PartitionKey, entity.RowKey, entity.ETag, stoppingToken).ConfigureAwait(false);
                        removed++;
                    }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 412)
                    {
                        // Deleted, or incremented under us — an incremented row has a live window again,
                        // and either way it is not this pass's to remove.
                    }
                }

                if (removed > 0)
                    logger.LogInformation("Swept {Count} expired rate-limit counter row(s)", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Rate-limit counter sweep failed; will retry on the next interval");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
