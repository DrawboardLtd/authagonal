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
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Deletes issued at once. Serial deletion could not keep up with the creation rate.</summary>
    /// <remarks>
    /// One round trip per row, issued one after another, put delete throughput orders of magnitude below the
    /// rate an anonymous endpoint creates rows at — so the retention this class exists to provide did not
    /// hold under exactly the traffic that makes it necessary. Bounded rather than unbounded because the
    /// table is already the one under the most write pressure and a sweep that throttles it is worse than a
    /// sweep that takes another tick.
    /// </remarks>
    private const int DeleteConcurrency = 16;

    /// <summary>
    /// Per-row failures tolerated in one pass before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Only 404 and 412 were handled per row, so any other <c>RequestFailedException</c> — including the 429
    /// that the very flood being swept produces on this table — escaped to the outer catch and abandoned the
    /// WHOLE pass until the next tick. The one condition under which the sweep is most needed was the one
    /// that stopped it. Now a failing row is counted and skipped; a pass gives up only when failures look
    /// systemic rather than incidental.
    /// </remarks>
    private const int MaxRowFailures = 100;

    /// <summary>The store's single row key per bucket partition.</summary>
    private const string CounterRowKey = "counter";

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
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
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


    /// <summary>
    /// One sweep pass. Returns what it removed and what it could not.
    /// </summary>
    /// <remarks>
    /// Separated from the timer loop so a test can drive a pass against real Table semantics. That matters
    /// more here than usual: the pass turns on a server-side OData filter, and a filter the service cannot
    /// render throws at query time — on the leader, inside a catch that logs and waits for the next tick,
    /// which is to say invisibly. Nothing short of a real query proves it parses.
    /// </remarks>
    internal async Task<(int Removed, int Failures)> SweepOnceAsync(CancellationToken stoppingToken)
    {
            var now = DateTimeOffset.UtcNow;
            var removed = 0;
            var failures = 0;

            // The expiry filter is applied SERVER-side. `RowKey eq 'counter'` alone is not a key filter —
            // Table Storage has no secondary index, so the query downloaded every row in the table and
            // discarded the live ones on this side. Adding the predicate does not make it a seek, but it
            // stops the live majority crossing the wire, and under the flood this exists to clean up the
            // live majority is nearly all of it.
            //
            // Written as an OData filter string rather than a LINQ expression on purpose: the SDK
            // translates only a small set of expression shapes, and `e.GetDateTimeOffset("ExpiresAt")` is
            // a method call it cannot render — it would have thrown at query time, on the leader, inside
            // the try that logs and waits for the next tick, i.e. silently.
            //
            // Consequence worth stating: a row carrying no ExpiresAt at all no longer matches, so it is
            // no longer collected. The store writes the property on both its create and its update path,
            // so no writer in this codebase produces one.
            var query = table.QueryAsync<TableEntity>(
                filter: TableClient.CreateQueryFilter($"RowKey eq {CounterRowKey} and ExpiresAt le {now}"),
                cancellationToken: stoppingToken);

            var inFlight = new List<Task>(DeleteConcurrency);

            async Task DeleteAsync(TableEntity entity)
            {
                try
                {
                    await table.DeleteEntityAsync(
                        entity.PartitionKey, entity.RowKey, entity.ETag, stoppingToken).ConfigureAwait(false);
                    Interlocked.Increment(ref removed);
                }
                catch (RequestFailedException ex) when (ex.Status is 404 or 412)
                {
                    // Deleted, or incremented under us — an incremented row has a live window again,
                    // and either way it is not this pass's to remove. Not a failure.
                }
                catch (RequestFailedException)
                {
                    // Throttling (429), a transient 5xx, anything else this row alone objects to. Counted
                    // and skipped: one uncooperative row must not cost the rest of the pass.
                    Interlocked.Increment(ref failures);
                }
            }

            await foreach (var entity in query.ConfigureAwait(false))
            {
                if (Volatile.Read(ref failures) > MaxRowFailures)
                {
                    logger.LogWarning(
                        "Rate-limit counter sweep abandoned after {Failures} row failures; {Removed} removed. "
                        + "The table is likely throttling — the next pass will continue.", failures, removed);
                    break;
                }

                inFlight.Add(DeleteAsync(entity));
                if (inFlight.Count < DeleteConcurrency) continue;

                await Task.WhenAll(inFlight).ConfigureAwait(false);
                inFlight.Clear();
            }

            await Task.WhenAll(inFlight).ConfigureAwait(false);

            if (removed > 0 || failures > 0)
                logger.LogInformation(
                    "Swept {Count} expired rate-limit counter row(s); {Failures} row(s) could not be removed",
                    removed, failures);

        return (removed, failures);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
