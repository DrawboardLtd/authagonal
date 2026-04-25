using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableOidcProviderStore(TableClient oidcProvidersTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IOidcProviderStore
{
    public async Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var response = await oidcProvidersTable.GetEntityAsync<OidcProviderEntity>(
                partitioner.PK(connectionId), OidcProviderEntity.ConfigRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OidcProviderConfig>();
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? oidcProvidersTable.QueryAsync<OidcProviderEntity>(
                e => e.RowKey == OidcProviderEntity.ConfigRowKey, cancellationToken: ct)
            : oidcProvidersTable.QueryAsync<OidcProviderEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == OidcProviderEntity.ConfigRowKey,
                cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default)
    {
        var entity = OidcProviderEntity.FromModel(config);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await oidcProvidersTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        try
        {
            await oidcProvidersTable.DeleteEntityAsync(pk, OidcProviderEntity.ConfigRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("OidcProviders", pk, OidcProviderEntity.ConfigRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
