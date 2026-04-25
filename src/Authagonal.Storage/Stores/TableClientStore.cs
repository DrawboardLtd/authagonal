using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableClientStore(TableClient clientsTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IClientStore
{
    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            var response = await clientsTable.GetEntityAsync<ClientEntity>(
                partitioner.PK(clientId), ClientEntity.ConfigRowKey, cancellationToken: ct);
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
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? clientsTable.QueryAsync<ClientEntity>(
                e => e.RowKey == ClientEntity.ConfigRowKey, cancellationToken: ct)
            : clientsTable.QueryAsync<ClientEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == ClientEntity.ConfigRowKey,
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
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await clientsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(clientId);
        try
        {
            await clientsTable.DeleteEntityAsync(pk, ClientEntity.ConfigRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Clients", pk, ClientEntity.ConfigRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
