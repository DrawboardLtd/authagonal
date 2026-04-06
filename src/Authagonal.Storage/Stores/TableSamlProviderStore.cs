using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableSamlProviderStore(TableClient samlProvidersTable, ITombstoneWriter? tombstoneWriter = null) : ISamlProviderStore
{
    public async Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var response = await samlProvidersTable.GetEntityAsync<SamlProviderEntity>(
                connectionId, SamlProviderEntity.ConfigRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SamlProviderConfig>();
        var query = samlProvidersTable.QueryAsync<SamlProviderEntity>(
            e => e.RowKey == SamlProviderEntity.ConfigRowKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default)
    {
        var entity = SamlProviderEntity.FromModel(config);
        await samlProvidersTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            await samlProvidersTable.DeleteEntityAsync(
                connectionId, SamlProviderEntity.ConfigRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("SamlProviders", connectionId, SamlProviderEntity.ConfigRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
