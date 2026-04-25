using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableScopeStore(TableClient scopesTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IScopeStore
{
    public async Task<Scope?> GetAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var response = await scopesTable.GetEntityAsync<ScopeEntity>(
                partitioner.PK(ScopeEntity.ScopePartition), name, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Scope>> ListAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(ScopeEntity.ScopePartition);
        var scopes = new List<Scope>();
        await foreach (var entity in scopesTable.QueryAsync<ScopeEntity>(
            e => e.PartitionKey == pk,
            cancellationToken: ct))
        {
            scopes.Add(entity.ToModel());
        }
        return scopes;
    }

    public async Task CreateAsync(Scope scope, CancellationToken ct = default)
    {
        var entity = ScopeEntity.FromModel(scope);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await scopesTable.AddEntityAsync(entity, ct);
    }

    public async Task UpdateAsync(Scope scope, CancellationToken ct = default)
    {
        var entity = ScopeEntity.FromModel(scope);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await scopesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var pk = partitioner.PK(ScopeEntity.ScopePartition);
        try
        {
            await scopesTable.DeleteEntityAsync(pk, name, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("Scopes", pk, name, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
