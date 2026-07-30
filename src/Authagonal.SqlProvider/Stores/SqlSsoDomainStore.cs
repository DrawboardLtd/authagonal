using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="ISsoDomainStore"/>. pk = lower-cased domain, sk = "mapping"; the connection id is
/// promoted to an attribute for <see cref="DeleteByConnectionAsync"/>.
/// </summary>
public sealed class SqlSsoDomainStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : ISsoDomainStore
{
    private const string MappingSk = "mapping";

    public async Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(domain.ToLowerInvariant()), MappingSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<SsoDomain>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SsoDomain>();
        await foreach (var row in table.QueryAsync(SqlFilters.Config(partitioner, MappingSk), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task UpsertAsync(SsoDomain domain, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(domain.Domain.ToLowerInvariant()), MappingSk)
        {
            Data = JsonSerializer.Serialize(domain, SqlJsonContext.Default.SsoDomain),
        };
        row.PutS("connectionId", domain.ConnectionId);
        return table.PutAsync(row, ct);
    }

    public async Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        var pk = partitioner.PK(domain.ToLowerInvariant());
        var old = await table.DeleteIfExistsReturningAsync(pk, MappingSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("SsoDomains", pk, MappingSk, ct).ConfigureAwait(false);
    }

    public async Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var filter = SqlFilters.Config(partitioner, MappingSk).WithAttr("connectionId", connectionId);

        var keys = new List<(string, string)>();
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
        {
            await table.DeleteAsync(row.Pk, row.Sk, ct).ConfigureAwait(false);
            keys.Add((row.Pk, row.Sk));
        }
        if (tombstones is not null && keys.Count > 0)
            await tombstones.WriteBatchAsync("SsoDomains", keys, ct).ConfigureAwait(false);
    }

    private static SsoDomain Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.SsoDomain)!;
}
