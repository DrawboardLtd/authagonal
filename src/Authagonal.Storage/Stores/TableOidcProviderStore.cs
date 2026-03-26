using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableOidcProviderStore(TableClient oidcProvidersTable) : IOidcProviderStore
{
    public async Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var response = await oidcProvidersTable.GetEntityAsync<OidcProviderEntity>(
                connectionId, OidcProviderEntity.ConfigRowKey, cancellationToken: ct);
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
        var query = oidcProvidersTable.QueryAsync<OidcProviderEntity>(
            e => e.RowKey == OidcProviderEntity.ConfigRowKey,
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
        await oidcProvidersTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            await oidcProvidersTable.DeleteEntityAsync(
                connectionId, OidcProviderEntity.ConfigRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
