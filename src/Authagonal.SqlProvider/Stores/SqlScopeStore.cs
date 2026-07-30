using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>SQL <see cref="IScopeStore"/>. All scopes share one partition ("scope"), sk = scope name.</summary>
public sealed class SqlScopeStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IScopeStore
{
    private const string Partition = "scope";

    public async Task<Scope?> GetAsync(string name, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), name, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<Scope>> ListAsync(CancellationToken ct = default)
    {
        var scopes = new List<Scope>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            scopes.Add(Read(row));
        return scopes;
    }

    public Task CreateAsync(Scope scope, CancellationToken ct = default) => Write(scope, ct);
    public Task UpdateAsync(Scope scope, CancellationToken ct = default) => Write(scope, ct);

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, name, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("Scopes", pk, name, ct).ConfigureAwait(false);
    }

    private Task Write(Scope scope, CancellationToken ct)
    {
        var row = new SqlRow(partitioner.PK(Partition), scope.Name)
        {
            Data = JsonSerializer.Serialize(scope, SqlJsonContext.Default.Scope),
        };
        return table.PutAsync(row, ct);
    }

    private static Scope Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.Scope)!;
}
