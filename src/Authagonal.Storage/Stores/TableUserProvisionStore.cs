using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableUserProvisionStore(TableClient tableClient) : IUserProvisionStore
{
    public async Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<UserProvision>();
        await foreach (var entity in tableClient.QueryAsync<UserProvisionEntity>(
            e => e.PartitionKey == userId, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    public async Task StoreAsync(UserProvision provision, CancellationToken ct = default)
    {
        var entity = UserProvisionEntity.FromModel(provision);
        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveAsync(string userId, string appId, CancellationToken ct = default)
    {
        try
        {
            await tableClient.DeleteEntityAsync(userId, appId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone
        }
    }

    public async Task RemoveAllByUserAsync(string userId, CancellationToken ct = default)
    {
        await foreach (var entity in tableClient.QueryAsync<UserProvisionEntity>(
            e => e.PartitionKey == userId, cancellationToken: ct))
        {
            try
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }
}
