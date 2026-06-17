using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableProvisioningAppStore(TableClient appsTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IProvisioningAppStore
{
    public async Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
    {
        try
        {
            var response = await appsTable.GetEntityAsync<ProvisioningAppEntity>(
                partitioner.PK(ProvisioningAppEntity.AppsPartition), appId, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(ProvisioningAppEntity.AppsPartition);
        var apps = new List<ProvisioningAppConfig>();
        await foreach (var entity in appsTable.QueryAsync<ProvisioningAppEntity>(
            e => e.PartitionKey == pk,
            cancellationToken: ct))
        {
            apps.Add(entity.ToModel());
        }
        return apps;
    }

    public async Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        var entity = ProvisioningAppEntity.FromModel(app);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await appsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(ProvisioningAppEntity.AppsPartition);
        try
        {
            await appsTable.DeleteEntityAsync(pk, appId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("ProvisioningApps", pk, appId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
