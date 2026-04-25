using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableScimTokenStore(TableClient scimTokensTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : IScimTokenStore
{
    public async Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        try
        {
            var response = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                partitioner.PK(tokenHash), ScimTokenEntity.LookupRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(clientId);
        var results = new List<ScimToken>();
        var query = scimTokensTable.QueryAsync<ScimTokenEntity>(
            e => e.PartitionKey == pk && e.RowKey.CompareTo(ScimTokenEntity.TokenRowKeyPrefix) >= 0
                 && e.RowKey.CompareTo("scimtoken\uffff") < 0,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task StoreAsync(ScimToken token, CancellationToken ct = default)
    {
        var forwardEntity = ScimTokenEntity.FromModelForward(token);
        forwardEntity.PartitionKey = partitioner.PK(forwardEntity.PartitionKey);
        var reverseEntity = ScimTokenEntity.FromModelReverse(token);
        reverseEntity.PartitionKey = partitioner.PK(reverseEntity.PartitionKey);

        await scimTokensTable.UpsertEntityAsync(forwardEntity, TableUpdateMode.Replace, ct);
        await scimTokensTable.UpsertEntityAsync(reverseEntity, TableUpdateMode.Replace, ct);
    }

    public async Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        try
        {
            var reverseEntity = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                partitioner.PK(clientId), $"{ScimTokenEntity.TokenRowKeyPrefix}{tokenId}", cancellationToken: ct);

            var token = reverseEntity.Value.ToModel();
            token.IsRevoked = true;

            var forwardEntity = ScimTokenEntity.FromModelForward(token);
            forwardEntity.PartitionKey = partitioner.PK(forwardEntity.PartitionKey);
            var updatedReverse = ScimTokenEntity.FromModelReverse(token);
            updatedReverse.PartitionKey = partitioner.PK(updatedReverse.PartitionKey);

            await scimTokensTable.UpsertEntityAsync(forwardEntity, TableUpdateMode.Replace, ct);
            await scimTokensTable.UpsertEntityAsync(updatedReverse, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task DeleteAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        try
        {
            var reverseRk = $"{ScimTokenEntity.TokenRowKeyPrefix}{tokenId}";
            var clientPk = partitioner.PK(clientId);
            var reverseEntity = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                clientPk, reverseRk, cancellationToken: ct);

            var tokenHash = reverseEntity.Value.TokenHash;
            var hashPk = partitioner.PK(tokenHash);

            // Delete forward index
            try
            {
                await scimTokensTable.DeleteEntityAsync(hashPk, ScimTokenEntity.LookupRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete reverse index
            await scimTokensTable.DeleteEntityAsync(clientPk, reverseRk, cancellationToken: ct);

            if (tombstoneWriter is not null)
            {
                await tombstoneWriter.WriteAsync("ScimTokens", hashPk, ScimTokenEntity.LookupRowKey, ct);
                await tombstoneWriter.WriteAsync("ScimTokens", clientPk, reverseRk, ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
