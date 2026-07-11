using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableScimGroupRoleMappingStore(TableClient table, EnvPartitioner partitioner, IChangeWriter? changeWriter = null) : IScimGroupRoleMappingStore
{
    public async Task<IReadOnlyList<ScimGroupRoleMapping>> GetAllAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(ScimGroupRoleMappingEntity.MappingPartition);
        var list = new List<ScimGroupRoleMapping>();
        await foreach (var entity in table.QueryAsync<ScimGroupRoleMappingEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            list.Add(entity.ToModel());
        }
        return list;
    }

    public async Task SetAsync(ScimGroupRoleMapping mapping, CancellationToken ct = default)
    {
        var entity = ScimGroupRoleMappingEntity.FromModel(mapping);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("ScimGroupRoleMappings", entity.PartitionKey, entity.RowKey, ct);
    }

    public async Task DeleteAsync(string groupId, string role, CancellationToken ct = default)
    {
        var pk = partitioner.PK(ScimGroupRoleMappingEntity.MappingPartition);
        var rk = ScimGroupRoleMappingEntity.RowKeyFor(groupId, role);
        if (changeWriter is not null)
            await changeWriter.WriteAsync("ScimGroupRoleMappings", pk, rk, ct);
        try
        {
            await table.DeleteEntityAsync(pk, rk, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
