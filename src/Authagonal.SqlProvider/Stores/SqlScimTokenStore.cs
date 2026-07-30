using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IScimTokenStore"/>. Dual index: a forward row (pk = tokenHash, sk = "lookup") for
/// O(1) auth, and a reverse row (pk = clientId, sk = "scimtoken|{tokenId}") to list by client. Both
/// carry the full token document and are kept in sync.
/// </summary>
public sealed class SqlScimTokenStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IScimTokenStore
{
    private const string Lookup = "lookup";
    private const string TokenPrefix = "scimtoken|";

    public async Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(tokenHash), Lookup, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default)
    {
        var results = new List<ScimToken>();
        var filter = SqlKeyFilter.Partition(partitioner.PK(clientId)) with { SkPrefix = TokenPrefix };
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public async Task StoreAsync(ScimToken token, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(token, SqlJsonContext.Default.ScimToken);

        var forward = new SqlRow(partitioner.PK(token.TokenHash), Lookup) { Data = json };
        var reverse = new SqlRow(partitioner.PK(token.ClientId), $"{TokenPrefix}{token.TokenId}") { Data = json };

        await table.PutAsync(forward, ct).ConfigureAwait(false);
        await table.PutAsync(reverse, ct).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        var reverse = await table.GetAsync(partitioner.PK(clientId), $"{TokenPrefix}{tokenId}", ct: ct).ConfigureAwait(false);
        if (reverse is null) return;

        var token = Read(reverse);
        token.IsRevoked = true;
        await StoreAsync(token, ct).ConfigureAwait(false); // rewrites both rows
    }

    public async Task DeleteAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        var clientPk = partitioner.PK(clientId);
        var reverseSk = $"{TokenPrefix}{tokenId}";
        var reverse = await table.GetAsync(clientPk, reverseSk, ct: ct).ConfigureAwait(false);
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

    private static ScimToken Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.ScimToken)!;
}
