using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableAgentProfileStore(
    TableClient agentsTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null) : IAgentProfileStore
{
    public async Task<AgentProfile?> GetAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            var entity = (await agentsTable.GetEntityAsync<AgentProfileEntity>(
                partitioner.PK(AgentProfileEntity.AgentsPartition), clientId, cancellationToken: ct)).Value;
            return entity.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<AgentProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(AgentProfileEntity.AgentsPartition);
        var profiles = new List<AgentProfile>();
        await foreach (var entity in agentsTable.QueryAsync<AgentProfileEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            profiles.Add(entity.ToModel());
        }
        return profiles;
    }

    public async Task UpsertAsync(AgentProfile profile, CancellationToken ct = default)
    {
        var entity = AgentProfileEntity.FromModel(profile);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await agentsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteUpsertAsync("AgentProfiles", entity.PartitionKey, entity.RowKey, ct);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(AgentProfileEntity.AgentsPartition);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("AgentProfiles", pk, clientId, ct);
        try
        {
            await agentsTable.DeleteEntityAsync(pk, clientId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
