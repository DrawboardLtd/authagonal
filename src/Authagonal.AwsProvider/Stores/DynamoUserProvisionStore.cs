using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IUserProvisionStore"/>. pk = userId, sk = appId.</summary>
public sealed class DynamoUserProvisionStore(DynamoTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IUserProvisionStore
{
    public async Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<UserProvision>();
        await foreach (var item in table.QueryAsync(partitioner.PK(userId), ct: ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task StoreAsync(UserProvision provision, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(provision.UserId), provision.AppId);
        item.PutS("data", JsonSerializer.Serialize(provision, AwsJsonContext.Default.UserProvision));
        return table.PutAsync(item, ct);
    }

    public async Task RemoveAsync(string userId, string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var old = await table.DeleteIfExistsReturningAsync(pk, appId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("UserProvisions", pk, appId, ct).ConfigureAwait(false);
    }

    public async Task RemoveAllByUserAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var item in table.QueryAsync(pk, ct: ct).ConfigureAwait(false))
        {
            var sk = item.GetStr(Dyn.Sk);
            await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
            keys.Add((pk, sk));
        }
        if (tombstones is not null && keys.Count > 0) await tombstones.WriteBatchAsync("UserProvisions", keys, ct).ConfigureAwait(false);
    }

    private static UserProvision Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.UserProvision)!;
}
