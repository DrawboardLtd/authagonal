using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>SQL <see cref="IOidcProviderStore"/>. pk = connectionId, sk = "config".</summary>
public sealed class SqlOidcProviderStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IOidcProviderStore
{
    private const string ConfigSk = "config";

    public async Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(connectionId), ConfigSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OidcProviderConfig>();
        await foreach (var row in table.QueryAsync(SqlFilters.Config(partitioner, ConfigSk), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(config.ConnectionId), ConfigSk)
        {
            Data = JsonSerializer.Serialize(config, SqlJsonContext.Default.OidcProviderConfig),
        };
        return table.PutAsync(row, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("OidcProviders", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static OidcProviderConfig Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.OidcProviderConfig)!;
}
