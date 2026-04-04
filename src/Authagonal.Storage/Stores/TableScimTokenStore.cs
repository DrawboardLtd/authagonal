using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableScimTokenStore(TableClient scimTokensTable) : IScimTokenStore
{
    public async Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        try
        {
            var response = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                tokenHash, ScimTokenEntity.LookupRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default)
    {
        var results = new List<ScimToken>();
        var query = scimTokensTable.QueryAsync<ScimTokenEntity>(
            e => e.PartitionKey == clientId && e.RowKey.CompareTo(ScimTokenEntity.TokenRowKeyPrefix) >= 0
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
        var reverseEntity = ScimTokenEntity.FromModelReverse(token);

        await scimTokensTable.UpsertEntityAsync(forwardEntity, TableUpdateMode.Replace, ct);
        await scimTokensTable.UpsertEntityAsync(reverseEntity, TableUpdateMode.Replace, ct);
    }

    public async Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        try
        {
            var reverseEntity = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                clientId, $"{ScimTokenEntity.TokenRowKeyPrefix}{tokenId}", cancellationToken: ct);

            var token = reverseEntity.Value.ToModel();
            token.IsRevoked = true;

            var forwardEntity = ScimTokenEntity.FromModelForward(token);
            var updatedReverse = ScimTokenEntity.FromModelReverse(token);

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
            var reverseEntity = await scimTokensTable.GetEntityAsync<ScimTokenEntity>(
                clientId, $"{ScimTokenEntity.TokenRowKeyPrefix}{tokenId}", cancellationToken: ct);

            var tokenHash = reverseEntity.Value.TokenHash;

            // Delete forward index
            try
            {
                await scimTokensTable.DeleteEntityAsync(tokenHash, ScimTokenEntity.LookupRowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete reverse index
            await scimTokensTable.DeleteEntityAsync(clientId, $"{ScimTokenEntity.TokenRowKeyPrefix}{tokenId}", cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
