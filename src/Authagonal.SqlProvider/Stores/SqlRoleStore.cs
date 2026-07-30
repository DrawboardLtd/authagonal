using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IRoleStore"/>. All roles share one partition ("role"), sk = roleId; the role name is
/// promoted to the <c>roleName</c> attribute for the by-name lookup.
/// </summary>
public sealed class SqlRoleStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IRoleStore
{
    private const string Partition = "role";

    public async Task<Role?> GetAsync(string roleId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), roleId, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<Role?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var filter = SqlKeyFilter.Partition(partitioner.PK(Partition)).WithAttr("roleName", name);
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
            return Read(row);
        return null;
    }

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default)
    {
        var roles = new List<Role>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            roles.Add(Read(row));
        return roles;
    }

    public Task CreateAsync(Role role, CancellationToken ct = default) => Write(role, ct);
    public Task UpdateAsync(Role role, CancellationToken ct = default) => Write(role, ct);

    public async Task DeleteAsync(string roleId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, roleId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("Roles", pk, roleId, ct).ConfigureAwait(false);
    }

    private Task Write(Role role, CancellationToken ct)
    {
        var row = new SqlRow(partitioner.PK(Partition), role.Id)
        {
            Data = JsonSerializer.Serialize(role, SqlJsonContext.Default.Role),
        };
        row.PutS("roleName", role.Name);
        return table.PutAsync(row, ct);
    }

    private static Role Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.Role)!;
}
