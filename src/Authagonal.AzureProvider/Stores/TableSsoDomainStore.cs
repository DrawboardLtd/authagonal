using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableSsoDomainStore(TableClient ssoDomainsTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null) : ISsoDomainStore
{
    public async Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default)
    {
        var normalizedDomain = domain.ToLowerInvariant();
        try
        {
            var response = await ssoDomainsTable.GetEntityAsync<SsoDomainEntity>(
                partitioner.PK(normalizedDomain), SsoDomainEntity.MappingRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SsoDomain>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SsoDomain>();
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? ssoDomainsTable.QueryAsync<SsoDomainEntity>(
                e => e.RowKey == SsoDomainEntity.MappingRowKey, cancellationToken: ct)
            : ssoDomainsTable.QueryAsync<SsoDomainEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == SsoDomainEntity.MappingRowKey,
                cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task UpsertAsync(SsoDomain domain, CancellationToken ct = default)
    {
        var entity = SsoDomainEntity.FromModel(domain);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await ssoDomainsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        var pk = partitioner.PK(domain.ToLowerInvariant());
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("SsoDomains", pk, SsoDomainEntity.MappingRowKey, ct);
        try
        {
            await ssoDomainsTable.DeleteEntityAsync(pk, SsoDomainEntity.MappingRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var entities = new List<SsoDomainEntity>();
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? ssoDomainsTable.QueryAsync<SsoDomainEntity>(
                e => e.RowKey == SsoDomainEntity.MappingRowKey && e.ConnectionId == connectionId,
                cancellationToken: ct)
            : ssoDomainsTable.QueryAsync<SsoDomainEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == SsoDomainEntity.MappingRowKey
                     && e.ConnectionId == connectionId,
                cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        // Tombstone-first (F24e): record the batch before deleting anything.
        if (tombstoneWriter is not null && entities.Count > 0)
            await tombstoneWriter.WriteBatchAsync("SsoDomains",
                entities.Select(e => (e.PartitionKey, e.RowKey)), ct);

        foreach (var entity in entities)
        {
            try
            {
                // entity.PartitionKey is already env-prefixed when read back (sandbox).
                await ssoDomainsTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }
}
