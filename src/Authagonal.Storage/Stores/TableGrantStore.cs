using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableGrantStore(
    TableClient grantsTable,
    TableClient grantsBySubjectTable) : IGrantStore
{
    public async Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        var hashedKey = HashKey(grant.Key);

        var grantEntity = GrantEntity.FromModel(grant, hashedKey);
        await grantsTable.UpsertEntityAsync(grantEntity, TableUpdateMode.Replace, ct);

        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var subjectEntity = GrantBySubjectEntity.FromModel(grant, hashedKey);
            await grantsBySubjectTable.UpsertEntityAsync(subjectEntity, TableUpdateMode.Replace, ct);
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
                catch (RequestFailedException ex) when (ex.Status == 404) { }
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

        foreach (var entity in entities)
        {
            // Delete from primary grants table
            try
            {
                await grantsTable.DeleteEntityAsync(entity.HashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
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

        foreach (var entity in entities)
        {
            // Delete from primary grants table
            try
            {
                await grantsTable.DeleteEntityAsync(entity.HashedKey, GrantEntity.GrantRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete from subject index
            try
            {
                await grantsBySubjectTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
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
        // Query all grants from the primary table that have expired
        var expiredEntities = new List<GrantEntity>();
        var query = grantsTable.QueryAsync<GrantEntity>(
            e => e.ExpiresAt <= cutoff,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            expiredEntities.Add(entity);
        }

        // Batch delete by partition key for efficiency
        var grantsByPartition = expiredEntities.GroupBy(e => e.PartitionKey);
        foreach (var group in grantsByPartition)
        {
            var batch = new List<TableTransactionAction>();
            foreach (var entity in group)
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));

                // Submit batch in chunks of 100 (Azure Table Storage limit)
                if (batch.Count >= 100)
                {
                    await grantsTable.SubmitTransactionAsync(batch, ct);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await grantsTable.SubmitTransactionAsync(batch, ct);
            }
        }

        // Clean up subject index entries
        foreach (var entity in expiredEntities)
        {
            if (!string.IsNullOrEmpty(entity.SubjectId))
            {
                var subjectRk = $"{entity.Type}|{entity.PartitionKey}";
                try
                {
                    await grantsBySubjectTable.DeleteEntityAsync(entity.SubjectId, subjectRk, cancellationToken: ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }
        }
    }

    internal static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(bytes);
    }
}
