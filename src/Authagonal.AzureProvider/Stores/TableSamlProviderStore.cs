using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableSamlProviderStore(TableClient samlProvidersTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null) : ISamlProviderStore
{
    public async Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var response = await samlProvidersTable.GetEntityAsync<SamlProviderEntity>(
                partitioner.PK(connectionId), SamlProviderEntity.ConfigRowKey, cancellationToken: ct);
            return response.Value.ToModel(partitioner);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SamlProviderConfig>();
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? samlProvidersTable.QueryAsync<SamlProviderEntity>(
                e => e.RowKey == SamlProviderEntity.ConfigRowKey, cancellationToken: ct)
            : samlProvidersTable.QueryAsync<SamlProviderEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == SamlProviderEntity.ConfigRowKey,
                cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel(partitioner));
        }

        return results;
    }

    public async Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default)
    {
        var entity = SamlProviderEntity.FromModel(config);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await samlProvidersTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("SamlProviders", pk, SamlProviderEntity.ConfigRowKey, ct);
        try
        {
            await samlProvidersTable.DeleteEntityAsync(pk, SamlProviderEntity.ConfigRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
