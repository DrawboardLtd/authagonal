using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IScimGroupRoleMappingStore"/>. All mappings share one partition; sk is
/// a stable hash of (groupId, role) since role names may contain key-unsafe characters.</summary>
public sealed class DynamoScimGroupRoleMappingStore(DynamoTable table, EnvPartitioner partitioner) : IScimGroupRoleMappingStore
{
    private const string Partition = "scimGroupRoleMapping";

    public async Task<IReadOnlyList<ScimGroupRoleMapping>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<ScimGroupRoleMapping>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            list.Add(Read(item));
        return list;
    }

    public Task SetAsync(ScimGroupRoleMapping mapping, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(Partition), RowKeyFor(mapping.GroupId, mapping.Role));
        item.PutS("data", JsonSerializer.Serialize(mapping, AwsJsonContext.Default.ScimGroupRoleMapping));
        return table.PutAsync(item, ct);
    }

    public Task DeleteAsync(string groupId, string role, CancellationToken ct = default)
        => table.DeleteAsync(partitioner.PK(Partition), RowKeyFor(groupId, role), ct);

    private static string RowKeyFor(string groupId, string role)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{groupId} {role}")));

    private static ScimGroupRoleMapping Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.ScimGroupRoleMapping)!;
}
