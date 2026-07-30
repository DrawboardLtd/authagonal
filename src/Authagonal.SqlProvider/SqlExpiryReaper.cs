using Authagonal.SqlProvider.Sql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider;

/// <summary>
/// Deletes rows whose <c>expires_at</c> has passed, on a timer.
/// <para>
/// This exists because neither backend expires rows on its own the way DynamoDB TTL does. Without it
/// the transient tables — SAML replay ids, OIDC federation state, MFA challenges, upstream refresh
/// tokens, the revocation list — would only ever grow: every one of those rows is already ignored on
/// read once expired, so the correctness is unaffected, but the tables are not. It is a
/// space-reclamation job, deliberately not the mechanism anything depends on for correctness.
/// </para>
/// <para>
/// Grant expiry is NOT handled here. Grants span three tables that have to be cleaned together (plus
/// tombstones for incremental backup), so <c>IGrantStore.RemoveExpiredAsync</c> owns it — reaping the
/// expiry-index rows independently would orphan the grants they point at.
/// </para>
/// </summary>
public sealed class SqlExpiryReaper(
    IReadOnlyList<SqlTable> tables, TimeSpan interval, ILogger<SqlExpiryReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var table in tables)
            {
                try
                {
                    var deleted = await table.DeleteExpiredAsync(now, stoppingToken).ConfigureAwait(false);
                    if (deleted > 0)
                        logger.LogDebug("Reaped {Count} expired rows from {Table}", deleted, table.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One table's failure must not stop the others, and the next tick retries anyway.
                    logger.LogWarning(ex, "Expiry sweep failed for {Table}", table.Name);
                }
            }
        }
    }
}
