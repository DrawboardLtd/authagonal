using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableUserProvisionStore(TableClient tableClient, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null) : IUserProvisionStore
{
    public async Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var results = new List<UserProvision>();
        await foreach (var entity in tableClient.QueryAsync<UserProvisionEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    public async Task StoreAsync(UserProvision provision, CancellationToken ct = default)
    {
        var entity = UserProvisionEntity.FromModel(provision);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task RemoveAsync(string userId, string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("UserProvisions", pk, appId, ct);
        try
        {
            await tableClient.DeleteEntityAsync(pk, appId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone
        }
    }

    public async Task RemoveAllByUserAsync(string userId, CancellationToken ct = default)
    {
        // Tombstone-first (F24e): record the batch before deleting anything.
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var entity in tableClient.QueryAsync<UserProvisionEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            keys.Add((entity.PartitionKey, entity.RowKey));
        }

        if (tombstoneWriter is not null && keys.Count > 0)
            await tombstoneWriter.WriteBatchAsync("UserProvisions", keys, ct);

        foreach (var (kpk, krk) in keys)
        {
            try
            {
                await tableClient.DeleteEntityAsync(kpk, krk, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }
}
