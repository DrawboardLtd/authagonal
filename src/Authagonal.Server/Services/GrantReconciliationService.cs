using Azure;
using Azure.Data.Tables;
using Authagonal.Storage.Entities;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class GrantReconciliationService(
    [FromKeyedServices("Grants")] TableClient grantsTable,
    [FromKeyedServices("GrantsBySubject")] TableClient grantsBySubjectTable,
    [FromKeyedServices("GrantsByExpiry")] TableClient grantsByExpiryTable,
    IOptions<BackgroundServiceOptions> bgOptions,
    ILogger<GrantReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(bgOptions.Value.GrantReconciliationDelayMinutes), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(bgOptions.Value.GrantReconciliationIntervalMinutes));

        do
        {
            try
            {
                var orphanedGrants = await RemoveOrphanedGrantsAsync(stoppingToken);
                var staleSubjectEntries = await RemoveStaleSubjectIndexEntriesAsync(stoppingToken);
                var staleExpiryEntries = await RemoveStaleExpiryIndexEntriesAsync(stoppingToken);

                if (orphanedGrants > 0 || staleSubjectEntries > 0 || staleExpiryEntries > 0)
                {
                    logger.LogInformation(
                        "Grant reconciliation completed: removed {OrphanedGrants} orphaned grants, {StaleSubjectEntries} stale subject index entries, {StaleExpiryEntries} stale expiry index entries",
                        orphanedGrants, staleSubjectEntries, staleExpiryEntries);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during grant reconciliation");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Finds grants with a SubjectId that have no matching GrantsBySubject entry and deletes them.
    /// These are dangerous orphans — they survive subject-based revocation.
    /// </summary>
    private async Task<int> RemoveOrphanedGrantsAsync(CancellationToken ct)
    {
        var removed = 0;
        var query = grantsTable.QueryAsync<GrantEntity>(cancellationToken: ct);

        await foreach (var grant in query)
        {
            if (string.IsNullOrEmpty(grant.SubjectId))
                continue;

            var subjectRk = $"{grant.Type}|{grant.PartitionKey}";
            try
            {
                await grantsBySubjectTable.GetEntityAsync<GrantBySubjectEntity>(
                    grant.SubjectId, subjectRk, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                logger.LogWarning(
                    "Deleting orphaned grant {HashedKey} for subject {SubjectId} — no matching subject index entry",
                    grant.PartitionKey, grant.SubjectId);

                try
                {
                    await grantsTable.DeleteEntityAsync(grant.PartitionKey, GrantEntity.GrantRowKey, cancellationToken: ct);
                    removed++;
                }
                catch (RequestFailedException deleteEx) when (deleteEx.Status == 404)
                {
                    // Already deleted by another process
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Finds GrantsBySubject entries whose referenced primary grant doesn't exist and deletes them.
    /// These are harmless but wasteful orphans.
    /// </summary>
    private async Task<int> RemoveStaleSubjectIndexEntriesAsync(CancellationToken ct)
    {
        var removed = 0;
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(cancellationToken: ct);

        await foreach (var indexEntry in query)
        {
            try
            {
                await grantsTable.GetEntityAsync<GrantEntity>(
                    indexEntry.HashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                logger.LogInformation(
                    "Deleting stale subject index entry for subject {SubjectId}, hashed key {HashedKey}",
                    indexEntry.PartitionKey, indexEntry.HashedKey);

                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(
                        indexEntry.PartitionKey, indexEntry.RowKey, cancellationToken: ct);
                    removed++;
                }
                catch (RequestFailedException deleteEx) when (deleteEx.Status == 404)
                {
                    // Already deleted by another process
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Finds GrantsByExpiry entries whose referenced primary grant doesn't exist and deletes them.
    /// </summary>
    private async Task<int> RemoveStaleExpiryIndexEntriesAsync(CancellationToken ct)
    {
        var removed = 0;
        var query = grantsByExpiryTable.QueryAsync<GrantByExpiryEntity>(cancellationToken: ct);

        await foreach (var indexEntry in query)
        {
            try
            {
                await grantsTable.GetEntityAsync<GrantEntity>(
                    indexEntry.RowKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                try
                {
                    await grantsByExpiryTable.DeleteEntityAsync(
                        indexEntry.PartitionKey, indexEntry.RowKey, cancellationToken: ct);
                    removed++;
                }
                catch (RequestFailedException deleteEx) when (deleteEx.Status == 404)
                {
                    // Already deleted by another process
                }
            }
        }

        return removed;
    }
}
