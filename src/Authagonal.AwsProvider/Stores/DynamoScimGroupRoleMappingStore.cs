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
    /// <remarks>
    /// The change writer is the reason this store's rows can appear in an incremental backup at all, and it
    /// was the one store on this backend that took none — every sibling on the same registration block got
    /// it. So role mappings were the single resource whose creations, changes and deletions were invisible
    /// to an incremental window, silently, on two of the three backends. Optional, like every sibling, so a
    /// host that wires the store by hand still works.
    /// </remarks>
public sealed class DynamoScimGroupRoleMappingStore(
    DynamoTable table, EnvPartitioner partitioner, IChangeWriter? changeWriter = null) : IScimGroupRoleMappingStore
{
    private const string Partition = "scimGroupRoleMapping";

    public async Task<IReadOnlyList<ScimGroupRoleMapping>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<ScimGroupRoleMapping>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            list.Add(Read(item));
        return list;
    }

    public async Task SetAsync(ScimGroupRoleMapping mapping, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var sk = RowKeyFor(mapping.GroupId, mapping.Role);
        var item = Dyn.Item(pk, sk);
        item.PutS("data", JsonSerializer.Serialize(mapping, AwsJsonContext.Default.ScimGroupRoleMapping));
        await table.PutAsync(item, ct).ConfigureAwait(false);
        if (changeWriter is not null)
            await changeWriter.WriteUpsertAsync("ScimGroupRoleMappings", pk, sk, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string groupId, string role, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var sk = RowKeyFor(groupId, role);
        // Change entry first, matching the Azure store: a delete that succeeds and then fails to record
        // itself leaves a row an incremental restore puts back.
        if (changeWriter is not null)
            await changeWriter.WriteAsync("ScimGroupRoleMappings", pk, sk, ct).ConfigureAwait(false);
        await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
    }

    private static string RowKeyFor(string groupId, string role)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{groupId} {role}")));

    private static ScimGroupRoleMapping Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.ScimGroupRoleMapping)!;
}
