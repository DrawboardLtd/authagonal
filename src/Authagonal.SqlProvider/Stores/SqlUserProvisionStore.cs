using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>SQL <see cref="IUserProvisionStore"/>. pk = userId, sk = appId.</summary>
public sealed class SqlUserProvisionStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IUserProvisionStore
{
    public async Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<UserProvision>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(userId), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task StoreAsync(UserProvision provision, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(provision.UserId), provision.AppId)
        {
            Data = JsonSerializer.Serialize(provision, SqlJsonContext.Default.UserProvision),
        };
        return table.PutAsync(row, ct);
    }

    public async Task RemoveAsync(string userId, string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var old = await table.DeleteIfExistsReturningAsync(pk, appId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("UserProvisions", pk, appId, ct).ConfigureAwait(false);
    }

    public async Task RemoveAllByUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var row in table.QueryPartitionAsync(pk, ct).ConfigureAwait(false))
        {
            await table.DeleteAsync(pk, row.Sk, ct).ConfigureAwait(false);
            keys.Add((pk, row.Sk));
        }
        if (tombstones is not null && keys.Count > 0)
            await tombstones.WriteBatchAsync("UserProvisions", keys, ct).ConfigureAwait(false);
    }

    private static UserProvision Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.UserProvision)!;
}
