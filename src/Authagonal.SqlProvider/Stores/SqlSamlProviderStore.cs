using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>SQL <see cref="ISamlProviderStore"/>. pk = connectionId, sk = "config".</summary>
public sealed class SqlSamlProviderStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : ISamlProviderStore
{
    private const string ConfigSk = "config";

    public async Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(connectionId), ConfigSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SamlProviderConfig>();
        await foreach (var row in table.QueryAsync(SqlFilters.Config(partitioner, ConfigSk), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(config.ConnectionId), ConfigSk)
        {
            Data = JsonSerializer.Serialize(config, SqlJsonContext.Default.SamlProviderConfig),
        };
        return table.PutAsync(row, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("SamlProviders", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static SamlProviderConfig Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.SamlProviderConfig)!;
}
