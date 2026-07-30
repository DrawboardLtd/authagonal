using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Microsoft.Extensions.Logging;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableGrantStore(
    TableClient grantsTable,
    TableClient grantsBySubjectTable,
    TableClient grantsByExpiryTable,
    EnvPartitioner partitioner,
    ILogger<TableGrantStore> logger,
    IChangeWriter? tombstoneWriter = null,
    IFieldCipher? fieldCipher = null) : IGrantStore
{
    // Encrypts PersistedGrant.Data at rest — refresh-token/auth-code/device/PAR payloads carry the
    // full OidcSubject (email, name, phone, claims). Defaults to a passthrough so single-tenant / OSS
    // hosts are unchanged; Cloud injects the per-tenant Vault Transit cipher when EncryptPii is on.
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private Task<string> ProtectAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ProtectAsync(data, ct);

    private Task<string> ResolveAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ResolveAsync(data, ct);

    public async Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        // GetAsync deliberately returns Key empty (only the hash is persisted), so a fetched grant
        // re-stored without re-setting Key would silently land in the SHA-256("") partition. Fail
        // loudly instead — callers must always know the plaintext handle they're writing under.
        if (string.IsNullOrEmpty(grant.Key))
            throw new ArgumentException(
                "PersistedGrant.Key is empty. Grants read back from storage have no Key — set it explicitly before storing.",
                nameof(grant));

        var hashedKey = HashKey(grant.Key);
        var protectedData = await ProtectAsync(grant.Data, ct);

        var grantEntity = GrantEntity.FromModel(grant, hashedKey);
        grantEntity.Data = protectedData;
        grantEntity.PartitionKey = partitioner.PK(grantEntity.PartitionKey);
        await grantsTable.UpsertEntityAsync(grantEntity, TableUpdateMode.Replace, ct);

        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var subjectEntity = GrantBySubjectEntity.FromModel(grant, hashedKey);
            subjectEntity.Data = protectedData;
            subjectEntity.PartitionKey = partitioner.PK(subjectEntity.PartitionKey);
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
                    await grantsTable.DeleteEntityAsync(partitioner.PK(hashedKey), GrantEntity.GrantRowKey, cancellationToken: ct);
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
            PartitionKey = partitioner.PK(GrantByExpiryEntity.GetPartitionKey(grant.ExpiresAt, hashedKey)),
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
                partitioner.PK(hashedKey), GrantEntity.GrantRowKey, cancellationToken: ct);
            var model = response.Value.ToModel();
            model.Data = await ResolveAsync(model.Data, ct);
            return model;
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
                partitioner.PK(hashedKey), GrantEntity.GrantRowKey, cancellationToken: ct);

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
                        partitioner.PK(entity.SubjectId), subjectRk, cancellationToken: ct);

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

    public async Task<bool> TryConsumeAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var hashedKeyPk = partitioner.PK(hashedKey);

        GrantEntity entity;
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKeyPk, GrantEntity.GrantRowKey, cancellationToken: ct);
            entity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }

        // Tombstone-first (F24e): a crash between a data delete and its tombstone loses the delete
        // from every future backup (the backstop only re-scans LIVE rows), so the tombstone goes down
        // before the row. A tombstone for a delete that then doesn't happen is safe: any later write
        // to the key re-stamps a newer storage Timestamp, and the merge keeps rows written after the
        // tombstone's (same-clock) DeletedAt.
        var expiryPartition = partitioner.PK(GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, hashedKey));
        if (tombstoneWriter is not null)
        {
            await tombstoneWriter.WriteAsync("Grants", hashedKeyPk, GrantEntity.GrantRowKey, ct);
            await tombstoneWriter.WriteAsync("GrantsByExpiry", expiryPartition, hashedKey, ct);
            if (!string.IsNullOrEmpty(entity.SubjectId))
                await tombstoneWriter.WriteAsync("GrantsBySubject", partitioner.PK(entity.SubjectId), $"{entity.Type}|{hashedKey}", ct);
        }

        // Atomic single-use: only the caller whose conditional (ETag) delete matches the current
        // row wins. A racing redemption gets 412/404 and loses.
        try
        {
            await grantsTable.DeleteEntityAsync(hashedKeyPk, GrantEntity.GrantRowKey, entity.ETag, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404)
        {
            return false;
        }

        // Best-effort index cleanup (mirrors RemoveAsync).
        try
        {
            await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, hashedKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        if (!string.IsNullOrEmpty(entity.SubjectId))
        {
            var subjectRk = $"{entity.Type}|{hashedKey}";
            var subjectPk = partitioner.PK(entity.SubjectId);
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(subjectPk, subjectRk, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        return true;
    }

    public async Task<bool> TryMarkConsumedAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(grant.Key))
            throw new ArgumentException(
                "PersistedGrant.Key is empty. Grants read back from storage have no Key — set it explicitly before marking consumed.",
                nameof(grant));

        var hashedKey = HashKey(grant.Key);
        var hashedKeyPk = partitioner.PK(hashedKey);

        GrantEntity entity;
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKeyPk, GrantEntity.GrantRowKey, cancellationToken: ct);
            entity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }

        // Already consumed by a racing caller — this caller lost the rotation race.
        if (entity.ConsumedAt is not null)
            return false;

        entity.ConsumedAt = grant.ConsumedAt ?? DateTimeOffset.UtcNow;
        entity.Data = await ProtectAsync(grant.Data, ct);

        // Atomic compare-and-set: the conditional (ETag / If-Match) update only lands for the caller
        // whose read matched the current row. A concurrent consume changes the ETag → 412 → this caller
        // loses. Without this, two readers that both observed ConsumedAt==null would both write a
        // consumed marker and neither would trip replay detection.
        try
        {
            await grantsTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404)
        {
            return false;
        }

        // Mirror the consumed marker to the subject index (best-effort, matches ConsumeAsync).
        if (!string.IsNullOrEmpty(entity.SubjectId))
        {
            var subjectRk = $"{entity.Type}|{hashedKey}";
            try
            {
                var subjectResponse = await grantsBySubjectTable.GetEntityAsync<GrantBySubjectEntity>(
                    partitioner.PK(entity.SubjectId), subjectRk, cancellationToken: ct);

                var subjectEntity = subjectResponse.Value;
                subjectEntity.ConsumedAt = entity.ConsumedAt;
                subjectEntity.Data = entity.Data;
                await grantsBySubjectTable.UpsertEntityAsync(subjectEntity, TableUpdateMode.Replace, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                logger.LogWarning("Subject index entry missing during consume-mark for subject {SubjectId}, key {HashedKey}",
                    entity.SubjectId, hashedKey);
            }
        }

        return true;
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var hashedKeyPk = partitioner.PK(hashedKey);

        // Get the grant first to find subject info for index cleanup
        try
        {
            var response = await grantsTable.GetEntityAsync<GrantEntity>(
                hashedKeyPk, GrantEntity.GrantRowKey, cancellationToken: ct);

            var entity = response.Value;

            // Tombstone-first (F24e) — see TryConsumeAsync.
            var expiryPartition = partitioner.PK(GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, hashedKey));
            if (tombstoneWriter is not null)
            {
                await tombstoneWriter.WriteAsync("Grants", hashedKeyPk, GrantEntity.GrantRowKey, ct);
                await tombstoneWriter.WriteAsync("GrantsByExpiry", expiryPartition, hashedKey, ct);
                if (!string.IsNullOrEmpty(entity.SubjectId))
                    await tombstoneWriter.WriteAsync("GrantsBySubject", partitioner.PK(entity.SubjectId), $"{entity.Type}|{hashedKey}", ct);
            }

            // Delete from primary table
            await grantsTable.DeleteEntityAsync(hashedKeyPk, GrantEntity.GrantRowKey, cancellationToken: ct);

            // Delete from expiry index
            try
            {
                await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, hashedKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            if (!string.IsNullOrEmpty(entity.SubjectId))
            {
                var subjectRk = $"{entity.Type}|{hashedKey}";
                var subjectPk = partitioner.PK(entity.SubjectId);
                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(subjectPk, subjectRk, cancellationToken: ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var subjectPk = partitioner.PK(subjectId);
        var entities = new List<GrantBySubjectEntity>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectPk,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        await DeleteIndexedGrantsAsync(entities, ct);
    }

    public async Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
    {
        var subjectPk = partitioner.PK(subjectId);
        var entities = new List<GrantBySubjectEntity>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectPk && e.ClientId == clientId,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        await DeleteIndexedGrantsAsync(entities, ct);
    }

    public async Task RemoveBySubjectAsync(
        string subjectId,
        IReadOnlyCollection<string> types,
        string? clientId = null,
        CancellationToken ct = default)
    {
        if (types.Count == 0) return;

        var subjectPk = partitioner.PK(subjectId);
        // Type is materialized on the index row (and leads its RowKey), so this needs no primary-table
        // reads. The type filter is applied client-side rather than in the OData filter because the set
        // is small and a multi-value `or` chain is both slower to plan and easy to get wrong.
        var wanted = new HashSet<string>(types, StringComparer.Ordinal);
        var entities = new List<GrantBySubjectEntity>();
        var query = clientId is null
            ? grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
                e => e.PartitionKey == subjectPk, cancellationToken: ct)
            : grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
                e => e.PartitionKey == subjectPk && e.ClientId == clientId, cancellationToken: ct);

        await foreach (var entity in query)
        {
            if (wanted.Contains(entity.Type))
                entities.Add(entity);
        }

        await DeleteIndexedGrantsAsync(entities, ct);
    }

    /// <summary>
    /// Deletes the primary row plus both index rows for every supplied subject-index entity.
    /// </summary>
    private async Task DeleteIndexedGrantsAsync(List<GrantBySubjectEntity> entities, CancellationToken ct)
    {
        // Tombstone-first (F24e): every key is derivable from the materialized index rows, so record
        // the whole batch before deleting anything — a crash mid-way can only leave rows that are
        // tombstoned AND still live, which later writes out-timestamp (safe), never a lost delete.
        if (tombstoneWriter is not null && entities.Count > 0)
        {
            await tombstoneWriter.WriteBatchAsync("Grants",
                entities.Select(e => (partitioner.PK(e.HashedKey), GrantEntity.GrantRowKey)), ct);
            await tombstoneWriter.WriteBatchAsync("GrantsByExpiry",
                entities.Select(e => (partitioner.PK(GrantByExpiryEntity.GetPartitionKey(e.ExpiresAt, e.HashedKey)), e.HashedKey)), ct);
            await tombstoneWriter.WriteBatchAsync("GrantsBySubject",
                entities.Select(e => (e.PartitionKey, e.RowKey)), ct);
        }

        foreach (var entity in entities)
        {
            var grantPk = partitioner.PK(entity.HashedKey);
            // Delete from primary grants table
            try
            {
                await grantsTable.DeleteEntityAsync(grantPk, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from expiry index
            var expiryPartition = partitioner.PK(GrantByExpiryEntity.GetPartitionKey(entity.ExpiresAt, entity.HashedKey));
            try
            {
                await grantsByExpiryTable.DeleteEntityAsync(expiryPartition, entity.HashedKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index — entity.PartitionKey already env-prefixed
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }

    public async Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var subjectPk = partitioner.PK(subjectId);
        var results = new List<PersistedGrant>();
        var query = grantsBySubjectTable.QueryAsync<GrantBySubjectEntity>(
            e => e.PartitionKey == subjectPk,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            var model = entity.ToModel();
            model.Data = await ResolveAsync(model.Data, ct);
            results.Add(model);
        }

        return results;
    }

    public async Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Query expiry index for entries in date buckets up to the cutoff.
        // PK format is "yyyy-MM-dd_N" (date-first, hash-spread across N slots),
        // so a single lexicographic range scan still captures all partitions up to cutoff.
        // For sandbox env, cap the scan to this env's prefix range so other envs'
        // expiries are untouched.
        var cutoffUpperBound = GrantByExpiryEntity.GetCutoffUpperBound(cutoff);
        var expiredEntries = new List<GrantByExpiryEntity>();

        var range = partitioner.RangeForEnv();
        var filter = range is null
            ? $"PartitionKey le '{cutoffUpperBound}'"
            : $"PartitionKey ge '{range.Value.Low}' and PartitionKey le '{partitioner.PK(cutoffUpperBound)}'";

        var query = grantsByExpiryTable.QueryAsync<GrantByExpiryEntity>(
            filter: filter, cancellationToken: ct);

        await foreach (var entity in query)
        {
            // Entries in the cutoff-day bucket may not all be expired yet
            if (entity.ExpiresAt <= cutoff)
                expiredEntries.Add(entity);
        }

        // Tombstone-first (F24e contract): record every delete before removing any row so an
        // incremental backup can never miss an expired-grant deletion (the backstop only re-scans
        // LIVE rows). Harmless today — grants aren't in the change-logged set — but keeps this delete
        // path consistent with every other store delete, so grants can join it without a silent gap.
        if (tombstoneWriter is not null && expiredEntries.Count > 0)
        {
            await tombstoneWriter.WriteBatchAsync("Grants",
                expiredEntries.Select(e => (partitioner.PK(e.RowKey), GrantEntity.GrantRowKey)), ct);
            await tombstoneWriter.WriteBatchAsync("GrantsByExpiry",
                expiredEntries.Select(e => (e.PartitionKey, e.RowKey)), ct);
            await tombstoneWriter.WriteBatchAsync("GrantsBySubject",
                expiredEntries.Where(e => !string.IsNullOrEmpty(e.SubjectId))
                              .Select(e => (partitioner.PK(e.SubjectId), $"{e.Type}|{e.RowKey}")), ct);
        }

        // Delete primary grants and subject index entries
        foreach (var entry in expiredEntries)
        {
            try
            {
                await grantsTable.DeleteEntityAsync(partitioner.PK(entry.RowKey), GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            if (!string.IsNullOrEmpty(entry.SubjectId))
            {
                var subjectRk = $"{entry.Type}|{entry.RowKey}";
                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(partitioner.PK(entry.SubjectId), subjectRk, cancellationToken: ct);
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
