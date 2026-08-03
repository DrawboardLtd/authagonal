using Azure;
using Azure.Data.Tables;
using Authagonal.AzureProvider.Entities;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Deletes grant rows and index entries that no longer refer to anything.
/// </summary>
/// <remarks>
/// It has to build the same keys <c>TableGrantStore</c> writes, and it was not building them.
/// <para>
/// The store writes Grants with <c>PartitionKey = partitioner.PK(hashedKey)</c> and GrantsBySubject with
/// <c>PartitionKey = partitioner.PK(subjectId)</c> and <c>RowKey = "{Type}|{hashedKey}"</c> from the RAW
/// hash. This service took the keyed <c>TableClient</c>s and reconstructed those keys with no
/// <see cref="EnvPartitioner"/> at all: it built the index RowKey from <c>grant.PartitionKey</c> — which
/// comes back from storage PREFIXED (<c>dev|&lt;hash&gt;</c>) while the index holds the raw hash — and looked
/// it up under the RAW subject while the index partition is <c>dev|&lt;subject&gt;</c>. Both halves miss, so
/// the 404 branch fired for every grant and the PRIMARY grant row was deleted. Under the live partitioner
/// nothing is prefixed and the keys agree, which is why this never showed up: the sweep is correct in
/// production and destroys every subject-bearing grant in any sandbox, dev or per-tenant env.
/// </para>
/// <para>
/// Every scan is also bounded to this env's partition range now, so a shared table set cannot have one env's
/// reconciliation reading — or deleting — another's rows.
/// </para>
/// </remarks>
public sealed class GrantReconciliationService(
    [FromKeyedServices("Grants")] TableClient grantsTable,
    [FromKeyedServices("GrantsBySubject")] TableClient grantsBySubjectTable,
    [FromKeyedServices("GrantsByExpiry")] TableClient grantsByExpiryTable,
    Authagonal.Core.Services.EnvPartitioner partitioner,
    IOptions<BackgroundServiceOptions> bgOptions,
    ILogger<GrantReconciliationService> logger) : BackgroundService
{
    /// <summary>
    /// Restricts a scan to this env's partitions. Null for the live env, which owns the unprefixed range.
    /// </summary>
    private string? EnvFilter()
    {
        if (partitioner.RangeForEnv() is not { } range) return null;
        return $"PartitionKey ge '{range.Low}' and PartitionKey lt '{range.High}'";
    }

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
        var query = grantsTable.QueryAsync<GrantEntity>(EnvFilter(), cancellationToken: ct);

        await foreach (var grant in query)
        {
            if (string.IsNullOrEmpty(grant.SubjectId))
                continue;

            // Strip on the way out, prefix on the way in — the index RowKey holds the RAW hash and its
            // partition holds the PREFIXED subject. Getting either wrong makes the lookup miss and deletes a
            // live grant.
            var subjectRk = $"{grant.Type}|{partitioner.Strip(grant.PartitionKey)}";
            try
            {
                await grantsBySubjectTable.GetEntityAsync<GrantBySubjectEntity>(
                    partitioner.PK(grant.SubjectId), subjectRk, cancellationToken: ct);
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
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(EnvFilter(), cancellationToken: ct);

        await foreach (var indexEntry in query)
        {
            try
            {
                // HashedKey is stored raw; the Grants partition is prefixed.
                await grantsTable.GetEntityAsync<GrantEntity>(
                    partitioner.PK(indexEntry.HashedKey), GrantEntity.GrantRowKey, cancellationToken: ct);
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
        var query = grantsByExpiryTable.QueryAsync<GrantByExpiryEntity>(EnvFilter(), cancellationToken: ct);

        await foreach (var indexEntry in query)
        {
            try
            {
                // Same asymmetry: the expiry index carries the raw hash as its RowKey.
                await grantsTable.GetEntityAsync<GrantEntity>(
                    partitioner.PK(indexEntry.RowKey), GrantEntity.GrantRowKey, cancellationToken: ct);
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
