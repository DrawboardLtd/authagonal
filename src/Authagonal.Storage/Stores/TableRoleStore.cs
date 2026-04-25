using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableRoleStore(TableClient rolesTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IRoleStore
{
    public async Task<Role?> GetAsync(string roleId, CancellationToken ct = default)
    {
        try
        {
            var response = await rolesTable.GetEntityAsync<RoleEntity>(
                partitioner.PK(RoleEntity.RolePartition), roleId, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Role?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var pk = partitioner.PK(RoleEntity.RolePartition);
        await foreach (var entity in rolesTable.QueryAsync<RoleEntity>(
            e => e.PartitionKey == pk && e.Name == name,
            cancellationToken: ct))
        {
            return entity.ToModel();
        }
        return null;
    }

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(RoleEntity.RolePartition);
        var roles = new List<Role>();
        await foreach (var entity in rolesTable.QueryAsync<RoleEntity>(
            e => e.PartitionKey == pk,
            cancellationToken: ct))
        {
            roles.Add(entity.ToModel());
        }
        return roles;
    }

    public async Task CreateAsync(Role role, CancellationToken ct = default)
    {
        var entity = RoleEntity.FromModel(role);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await rolesTable.AddEntityAsync(entity, ct);
    }

    public async Task UpdateAsync(Role role, CancellationToken ct = default)
    {
        var entity = RoleEntity.FromModel(role);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await rolesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string roleId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(RoleEntity.RolePartition);
        try
        {
            await rolesTable.DeleteEntityAsync(pk, roleId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Roles", pk, roleId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
