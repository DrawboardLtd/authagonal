using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;
using Microsoft.Extensions.Logging;

namespace Authagonal.Storage.Stores;

public sealed class TableGrantStore(
    TableClient grantsTable,
    TableClient grantsBySubjectTable,
    TableClient grantsByExpiryTable,
    ILogger<TableGrantStore> logger,
    ITombstoneWriter? tombstoneWriter = null) : IGrantStore
{
    public async Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        var hashedKey = HashKey(grant.Key);

        var grantEntity = GrantEntity.FromModel(grant, hashedKey);
        await grantsTable.UpsertEntityAsync(grantEntity, TableUpdateMode.Replace, ct);

        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var subjectEntity = GrantBySubjectEntity.FromModel(grant, hashedKey);
            try
            {
                await grantsBySubjectTable.UpsertEntityAsync(subjectEntity, TableUpdateMode.Replace, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to write subject index for grant {HashedKey}, subject {SubjectId}. Compensating by deleting primary grant",
                    hashedKey, grant.SubjectId);

                try
                {
                    await grantsTable.DeleteEntityAsync(hashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
                }
                catch (Exception compensateEx) when (compensateEx is not OperationCanceledException)
                {
                    logger.LogCritical(compensateEx,
                        "CRITICAL: Failed to compensate-delete primary grant {HashedKey} after subject index write failure. Orphaned grant requires manual reconciliation",
                        hashedKey);
                }

                throw;
            }
        }

        // Write expiry index for efficient cleanup queries
        var expiryEntity = new GrantByExpiryEntity
        {
            PartitionKey = GrantByExpiryEntity.GetPartitionKey(grant.ExpiresAt, hashedKey),
            RowKey = hashedKey,
            SubjectId = grant.SubjectId,
            Type = grant.Type,
            ExpiresAt = grant.ExpiresAt
        };

        try
        {
            await grantsByExpiryTable.UpsertEntityAsync(expiryEntity, TableUpdateMode.Replace, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write expiry index for grant {HashedKey}. Grant will still be cleaned up by reconciliation", hashedKey);
        }
    }

    public async Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task ConsumeAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);

            var entity = response.Value;
            entity.ConsumedAt = DateTimeOffset.UtcNow;
            await grantsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

            // Also update the subject index if subject exists
            if (!string.IsNullOrEmpty(entity.SubjectId))
            {
                var subjectRk = $"{entity.Type}|{hashedKey}";
                try
                {
                    var subjectResponse = await grantsBySubjectTable.GetEntityAsync<GrantBySubjectEntity>(
                        entity.SubjectId, subjectRk, cancellationToken: ct);

                    var subjectEntity = subjectResponse.Value;
                    subjectEntity.ConsumedAt = entity.ConsumedAt;
                    await grantsBySubjectTable.UpsertEntityAsync(subjectEntity, TableUpdateMode.Replace, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    logger.LogWarning("Subject index entry missing during consume for subject {SubjectId}, key {HashedKey}",
                        entity.SubjectId, hashedKey);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);

        // Get the grant first to find subject info for index cleanup
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);

            var entity = response.Value;

            // Delete from primary table
            await grantsTable.DeleteEntityAsync(hashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);

            // Delete from expiry index
            var expiryPartition = GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, hashedKey);
            try
            {
                await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, hashedKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            if (!string.IsNullOrEmpty(entity.SubjectId))
            {
                var subjectRk = $"{entity.Type}|{hashedKey}";
                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(entity.SubjectId, subjectRk, cancellationToken: ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            if (tombstoneWriter is not null)
            {
                await tombstoneWriter.WriteAsync("Grants", hashedKey, GrantEntity.GrantRowKey, ct);
                await tombstoneWriter.WriteAsync("GrantsByExpiry", expiryPartition, hashedKey, ct);
                if (!string.IsNullOrEmpty(entity.SubjectId))
                    await tombstoneWriter.WriteAsync("GrantsBySubject", entity.SubjectId, $"{entity.Type}|{hashedKey}", ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var entities = new List<GrantBySubjectEntity>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectId,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        var grantTombstones = new List<(string, string)>();
        var expiryTombstones = new List<(string, string)>();
        var subjectTombstones = new List<(string, string)>();

        foreach (var entity in entities)
        {
            // Delete from primary grants table
            try
            {
                await grantsTable.DeleteEntityAsync(entity.HashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
                grantTombstones.Add((entity.HashedKey, GrantEntity.GrantRowKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from expiry index
            var expiryPartition = GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, entity.HashedKey);
            try
            {
                await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, entity.HashedKey, cancellationToken: ct);
                expiryTombstones.Add((expiryPartition, entity.HashedKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                subjectTombstones.Add((entity.PartitionKey, entity.RowKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        if (tombstoneWriter is not null)
        {
            await tombstoneWriter.WriteBatchAsync("Grants", grantTombstones, ct);
            await tombstoneWriter.WriteBatchAsync("GrantsByExpiry", expiryTombstones, ct);
            await tombstoneWriter.WriteBatchAsync("GrantsBySubject", subjectTombstones, ct);
        }
    }

    public async Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
    {
        var entities = new List<GrantBySubjectEntity>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectId && e.ClientId == clientId,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        var grantTombstones = new List<(string, string)>();
        var expiryTombstones = new List<(string, string)>();
        var subjectTombstones = new List<(string, string)>();

        foreach (var entity in entities)
        {
            // Delete from primary grants table
            try
            {
                await grantsTable.DeleteEntityAsync(entity.HashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
                grantTombstones.Add((entity.HashedKey, GrantEntity.GrantRowKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from expiry index
            var expiryPartition = GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, entity.HashedKey);
            try
            {
                await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, entity.HashedKey, cancellationToken: ct);
                expiryTombstones.Add((expiryPartition, entity.HashedKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                subjectTombstones.Add((entity.PartitionKey, entity.RowKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        if (tombstoneWriter is not null)
        {
            await tombstoneWriter.WriteBatchAsync("Grants", grantTombstones, ct);
            await tombstoneWriter.WriteBatchAsync("GrantsByExpiry", expiryTombstones, ct);
            await tombstoneWriter.WriteBatchAsync("GrantsBySubject", subjectTombstones, ct);
        }
    }

    public async Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var results = new List<PersistedGrant>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectId,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Query expiry index for entries in date buckets up to the cutoff.
        // PK format is "yyyy-MM-dd_N" (date-first, hash-spread across N slots),
        // so a single lexicographic range scan still captures all partitions up to cutoff.
        var cutoffUpperBound = GrantByExpiryEntity.GetCutoffUpperBound(cutoff);
        var expiredEntries = new List<GrantByExpiryEntity>();

        var query = grantsByExpiryTable.QueryAsync<GrantByExpiryEntity>(
            filter: $"PartitionKey le '{cutoffUpperBound}'",
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            // Entries in the cutoff-day bucket may not all be expired yet
            if (entity.ExpiresAt <= cutoff)
                expiredEntries.Add(entity);
        }

        // Delete primary grants and subject index entries
        foreach (var entry in expiredEntries)
        {
            try
            {
                await grantsTable.DeleteEntityAsync(entry.RowKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            if (!string.IsNullOrEmpty(entry.SubjectId))
            {
                var subjectRk = $"{entry.Type}|{entry.RowKey}";
                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(entry.SubjectId, subjectRk, cancellationToken: ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }
        }

        // Batch delete expiry index entries by date bucket
        var byBucket = expiredEntries.GroupBy(e => e.PartitionKey);
        foreach (var group in byBucket)
        {
            var batch = new List<TableTransactionAction>();
            foreach (var entity in group)
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));

                if (batch.Count >= 100)
                {
                    await grantsByExpiryTable.SubmitTransactionAsync(batch, ct);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await grantsByExpiryTable.SubmitTransactionAsync(batch, ct);
        }
    }

    public static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(bytes);
    }
}
