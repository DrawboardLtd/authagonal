using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IRoleStore"/>. All roles share one partition ("role"), sk = roleId;
/// the role name is promoted to <c>roleName</c> for the by-name lookup ("name" is a DynamoDB reserved
/// word, so the attribute is renamed).</summary>
public sealed class DynamoRoleStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : IRoleStore
{
    private const string Partition = "role";

    public async Task<Role?> GetAsync(string roleId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), roleId, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<Role?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await foreach (var item in table.QueryAsync(
            partitioner.PK(Partition),
            filterExpression: "roleName = :n",
            values: new Dictionary<string, AttributeValue> { [":n"] = new() { S = name } },
            ct: ct).ConfigureAwait(false))
        {
            return Read(item);
        }
        return null;
    }

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default)
    {
        var roles = new List<Role>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            roles.Add(Read(item));
        return roles;
    }

    public Task CreateAsync(Role role, CancellationToken ct = default) => Write(role, ct);
    public Task UpdateAsync(Role role, CancellationToken ct = default) => Write(role, ct);

    public async Task DeleteAsync(string roleId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, roleId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("Roles", pk, roleId, ct).ConfigureAwait(false);
    }

    private Task Write(Role role, CancellationToken ct)
    {
        var item = Dyn.Item(partitioner.PK(Partition), role.Id);
        item.PutS("data", JsonSerializer.Serialize(role, AwsJsonContext.Default.Role));
        item.PutS("roleName", role.Name);
        return table.PutAsync(item, ct);
    }

    private static Role Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.Role)!;
}
