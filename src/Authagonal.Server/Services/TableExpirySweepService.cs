using Authagonal.Core.Clustering;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Server.Services;

/// <summary>
/// Deletes expired rows from one Azure Table on a timer, on the leader only.
/// </summary>
/// <remarks>
/// <para>
/// Azure Table Storage has no TTL. DynamoDB expires rows through its native TTL attribute and SQL rows are
/// collected by <c>SqlExpiryReaper</c>, whose table list covers <c>MfaChallenges</c>,
/// <c>UpstreamRefreshTokens</c> and <c>RevokedTokens</c> — so on those two backends all three are collected by
/// the machinery the store already runs. On Azure, nothing collected any of them, and Azure Table is the
/// DEFAULT and the documented quick start (<c>docker compose up</c>, the README, every appsettings example).
/// </para>
/// <para>
/// All three accumulate on ordinary traffic, with no attacker involved:
/// </para>
/// <list type="bullet">
/// <item>
/// A user completes the password step and abandons the second factor. <c>TableMfaStore</c> writes an
/// <c>MfaChallenges</c> row and deletes it only on successful consumption, so the row — user id, client id,
/// return URL, WebAuthn challenge — is permanent.
/// </item>
/// <item>
/// Every federated session writes an <c>UpstreamRefreshTokens</c> row holding the upstream IdP's refresh
/// token. That is a live credential for another provider, retained forever.
/// </item>
/// <item>
/// Every revocation writes a <c>RevokedTokens</c> row whose own comment says the entry "lives exactly as long
/// as the token it kills and the stores' existing expiry reapers keep the list bounded" — a reaper this
/// backend did not have.
/// </item>
/// </list>
/// <para>
/// Parameterised rather than written three times, because the differences are a table and a column name and
/// the sweep is otherwise identical. It follows <see cref="RateLimitCounterSweepService"/> exactly, including
/// its two hard-won details: the expiry predicate is a server-side OData filter written as a string (the SDK
/// cannot render <c>e.GetDateTimeOffset(...)</c> and would throw at query time, on the leader, inside a catch
/// that logs and waits — i.e. invisibly), and a single uncooperative row is counted and skipped rather than
/// abandoning the whole pass, since the throttling that makes a sweep necessary is exactly what used to stop
/// it.
/// </para>
/// <para>
/// Correctness never depends on the sweep. Every one of these rows is already rejected on read by its own
/// expiry check, so a stale row cannot be consumed, honoured or counted. This is retention only — which is
/// also why an expiry column that is NULL is deliberately left alone: on
/// <c>UpstreamRefreshTokens</c> that means "no expiry known", and a sweep must not invent one for a
/// credential.
/// </para>
/// </remarks>
internal sealed class TableExpirySweepService(
    TableClient table,
    string expiryProperty,
    string description,
    ILeaderElection election,
    ILogger<TableExpirySweepService> logger) : BackgroundService
{
    /// <summary>
    /// Less frequent than the rate-limit sweep: these rows are created per sign-in rather than per request,
    /// and none of them is read after expiry regardless.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private const int DeleteConcurrency = 16;
    private const int MaxRowFailures = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            // Leader only: every node scanning the same table and racing each other's deletes buys nothing.
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
                logger.LogWarning(ex,
                    "Expiry sweep for {Description} failed; will retry on the next interval", description);
            }
        }
    }

    /// <summary>
    /// One sweep pass. Returns what it removed and what it could not.
    /// </summary>
    /// <remarks>
    /// Internal so a test can drive a pass against real Table semantics, which is the only thing that proves
    /// the OData filter parses — see the note on the filter below.
    /// </remarks>
    internal async Task<(int Removed, int Failures)> SweepOnceAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        var failures = 0;

        // Server-side, and built through CreateQueryFilter so the column name is interpolated as an
        // identifier and the timestamp is rendered in the form Table Storage expects. A row whose expiry
        // column is null does not match `le`, which is the intended behaviour — see the remarks.
        var filter = $"{expiryProperty} le datetime'{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'";

        var query = table.QueryAsync<TableEntity>(filter: filter, cancellationToken: stoppingToken);

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
                // Consumed, or rewritten under us — either way not this pass's to remove. Not a failure.
            }
            catch (RequestFailedException)
            {
                // Throttling, a transient 5xx, anything else this row alone objects to: counted and skipped.
                Interlocked.Increment(ref failures);
            }
        }

        await foreach (var entity in query.ConfigureAwait(false))
        {
            if (Volatile.Read(ref failures) > MaxRowFailures)
            {
                logger.LogWarning(
                    "Expiry sweep for {Description} abandoned after {Failures} row failures; {Removed} removed. "
                    + "The table is likely throttling — the next pass will continue.",
                    description, failures, removed);
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
                "Swept {Count} expired {Description} row(s); {Failures} row(s) could not be removed",
                removed, description, failures);

        return (removed, failures);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
