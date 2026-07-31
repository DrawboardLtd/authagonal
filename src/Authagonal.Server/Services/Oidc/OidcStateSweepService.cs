using Authagonal.Core.Clustering;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Oidc;

/// <summary>
/// Deletes abandoned OIDC federation-state rows on a timer, on the leader only.
/// </summary>
/// <remarks>
/// <c>/oidc/{connectionId}/login</c> is anonymous and writes one of these rows on every hit, before
/// any authentication, and <see cref="OidcStateStore.ConsumeAsync"/> removes it only when the flow
/// completes. Azure Table has no TTL and nothing swept the table, so every abandoned federation
/// attempt left a permanent row holding a code verifier and a nonce. The SQL provider has covered
/// this since it shipped (<c>SqlExpiryReaper</c>); this is the same job for the Azure path.
/// <para>
/// Correctness never depended on it — <c>ConsumeAsync</c> refuses a row older than the configured
/// lifetime, and a missing timestamp reads as invalid. This is retention, and the retention is of
/// rows an unauthenticated caller can create at will.
/// </para>
/// </remarks>
internal sealed class OidcStateSweepService(
    [FromKeyedServices("OidcStateStore")] TableClient table,
    IOptions<CacheOptions> cacheOptions,
    ILeaderElection election,
    ILogger<OidcStateSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Rows are removed once they are well past unusable. The margin is deliberate: deleting on the
    /// exact lifetime would race a callback arriving at the edge of it, turning a valid-but-late
    /// federation return into a confusing "unknown state" instead of the expiry it actually is.
    /// </summary>
    private static readonly TimeSpan RetentionMargin = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            // Leader only. Every node running this would have them all scanning the same table and
            // racing each other's deletes for no benefit.
            if (!election.IsLeader) continue;

            try
            {
                var cutoff = DateTimeOffset.UtcNow
                    - TimeSpan.FromMinutes(cacheOptions.Value.OidcStateLifetimeMinutes)
                    - RetentionMargin;

                var removed = 0;
                await foreach (var entity in table
                    .QueryAsync<TableEntity>(e => e.RowKey == "state", cancellationToken: stoppingToken)
                    .ConfigureAwait(false))
                {
                    // Timestamp rather than the CreatedAt property: it is service-assigned, always
                    // present, and a row written without CreatedAt is invalid to ConsumeAsync anyway,
                    // so it should be swept too rather than kept forever for lack of a field.
                    if (entity.Timestamp is not { } written || written > cutoff) continue;

                    try
                    {
                        await table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, stoppingToken)
                            .ConfigureAwait(false);
                        removed++;
                    }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 412)
                    {
                        // Consumed or rewritten under us. Either way it is not ours to delete.
                    }
                }

                if (removed > 0)
                    logger.LogInformation("Swept {Count} abandoned OIDC federation state row(s)", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OIDC federation state sweep failed; will retry on the next interval");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
