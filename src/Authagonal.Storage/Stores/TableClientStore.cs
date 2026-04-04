using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableClientStore(TableClient clientsTable) : IClientStore
{
    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            var response = await clientsTable.GetEntityAsync<ClientEntity>(
                clientId, ClientEntity.ConfigRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OAuthClient>();
        var query = clientsTable.QueryAsync<ClientEntity>(
            e => e.RowKey == ClientEntity.ConfigRowKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
    {
        var entity = ClientEntity.FromModel(client);
        await clientsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            await clientsTable.DeleteEntityAsync(clientId, ClientEntity.ConfigRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
