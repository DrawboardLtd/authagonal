using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IScimTokenStore"/>. Dual index: a forward row (pk = tokenHash,
/// sk = "lookup") for O(1) auth, and a reverse row (pk = clientId, sk = "scimtoken|{tokenId}") to list
/// by client. Both carry the full token document and are kept in sync.</summary>
public sealed class DynamoScimTokenStore(DynamoTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IScimTokenStore
{
    private const string Lookup = "lookup";
    private const string TokenPrefix = "scimtoken|";

    public async Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(tokenHash), Lookup, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default)
    {
        var results = new List<ScimToken>();
        await foreach (var item in table.QueryAsync(
            partitioner.PK(clientId),
            sortKeyCondition: "begins_with(sk, :p)",
            values: new Dictionary<string, AttributeValue> { [":p"] = new() { S = TokenPrefix } },
            ct: ct).ConfigureAwait(false))
        {
            results.Add(Read(item));
        }
        return results;
    }

    public async Task StoreAsync(ScimToken token, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(token, AwsJsonContext.Default.ScimToken);

        var forward = Dyn.Item(partitioner.PK(token.TokenHash), Lookup);
        forward.PutS("data", json);
        var reverse = Dyn.Item(partitioner.PK(token.ClientId), $"{TokenPrefix}{token.TokenId}");
        reverse.PutS("data", json);

        await table.PutAsync(forward, ct).ConfigureAwait(false);
        await table.PutAsync(reverse, ct).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        var reverse = await table.GetAsync(partitioner.PK(clientId), $"{TokenPrefix}{tokenId}", ct).ConfigureAwait(false);
        if (reverse is null) return;

        var token = Read(reverse);
        token.IsRevoked = true;
        await StoreAsync(token, ct).ConfigureAwait(false); // rewrites both rows
    }

    public async Task DeleteAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        var clientPk = partitioner.PK(clientId);
        var reverseSk = $"{TokenPrefix}{tokenId}";
        var reverse = await table.GetAsync(clientPk, reverseSk, ct).ConfigureAwait(false);
        if (reverse is null) return;

        var hashPk = partitioner.PK(Read(reverse).TokenHash);
        await table.DeleteAsync(hashPk, Lookup, ct).ConfigureAwait(false);
        await table.DeleteAsync(clientPk, reverseSk, ct).ConfigureAwait(false);

        if (tombstones is not null)
        {
            await tombstones.WriteAsync("ScimTokens", hashPk, Lookup, ct).ConfigureAwait(false);
            await tombstones.WriteAsync("ScimTokens", clientPk, reverseSk, ct).ConfigureAwait(false);
        }
    }

    private static ScimToken Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.ScimToken)!;
}
