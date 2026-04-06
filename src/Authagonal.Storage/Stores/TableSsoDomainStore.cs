using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableSsoDomainStore(TableClient ssoDomainsTable, ITombstoneWriter? tombstoneWriter = null) : ISsoDomainStore
{
    public async Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default)
    {
        var normalizedDomain = domain.ToLowerInvariant();
        try
        {
            var response = await ssoDomainsTable.GetEntityAsync<SsoDomainEntity>(
                normalizedDomain, SsoDomainEntity.MappingRowKey, cancellationToken: ct);
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
        var query = ssoDomainsTable.QueryAsync<SsoDomainEntity>(
            e => e.RowKey == SsoDomainEntity.MappingRowKey,
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
        await ssoDomainsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        var normalizedDomain = domain.ToLowerInvariant();
        try
        {
            await ssoDomainsTable.DeleteEntityAsync(
                normalizedDomain, SsoDomainEntity.MappingRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("SsoDomains", normalizedDomain, SsoDomainEntity.MappingRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var entities = new List<SsoDomainEntity>();
        var query = ssoDomainsTable.QueryAsync<SsoDomainEntity>(
            e => e.RowKey == SsoDomainEntity.MappingRowKey && e.ConnectionId == connectionId,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            entities.Add(entity);
        }

        var tombstones = new List<(string, string)>();
        foreach (var entity in entities)
        {
            try
            {
                await ssoDomainsTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                tombstones.Add((entity.PartitionKey, entity.RowKey));
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        if (tombstoneWriter is not null && tombstones.Count > 0)
            await tombstoneWriter.WriteBatchAsync("SsoDomains", tombstones, ct);
    }
}
