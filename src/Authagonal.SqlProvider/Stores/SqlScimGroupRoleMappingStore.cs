using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IScimGroupRoleMappingStore"/>. All mappings share one partition; sk is a stable hash
/// of (groupId, role) since role names may contain key-unsafe characters.
/// </summary>
public sealed class SqlScimGroupRoleMappingStore(SqlTable table, EnvPartitioner partitioner) : IScimGroupRoleMappingStore
{
    private const string Partition = "scimGroupRoleMapping";

    public async Task<IReadOnlyList<ScimGroupRoleMapping>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<ScimGroupRoleMapping>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            list.Add(Read(row));
        return list;
    }

    public Task SetAsync(ScimGroupRoleMapping mapping, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(Partition), RowKeyFor(mapping.GroupId, mapping.Role))
        {
            Data = JsonSerializer.Serialize(mapping, SqlJsonContext.Default.ScimGroupRoleMapping),
        };
        return table.PutAsync(row, ct);
    }

    public Task DeleteAsync(string groupId, string role, CancellationToken ct = default)
        => table.DeleteAsync(partitioner.PK(Partition), RowKeyFor(groupId, role), ct);

    private static string RowKeyFor(string groupId, string role)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{groupId} {role}")));

    private static ScimGroupRoleMapping Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.ScimGroupRoleMapping)!;
}
