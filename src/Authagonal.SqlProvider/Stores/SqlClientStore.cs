using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IClientStore"/>. One row per client: pk = client_id, sk = "config", with the whole
/// <see cref="OAuthClient"/> in the document column (no field is queried server-side).
/// </summary>
public sealed class SqlClientStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IClientStore
{
    private const string ConfigSk = "config";

    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(clientId), ConfigSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OAuthClient>();
        await foreach (var row in table.QueryAsync(SqlFilters.Config(partitioner, ConfigSk), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(client.ClientId), ConfigSk)
        {
            Data = JsonSerializer.Serialize(client, SqlJsonContext.Default.OAuthClient),
        };
        return table.PutAsync(row, ct);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(clientId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("Clients", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static OAuthClient Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.OAuthClient)!;
}
