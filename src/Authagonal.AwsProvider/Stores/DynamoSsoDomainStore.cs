using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="ISsoDomainStore"/>. pk = lower-cased domain, sk = "mapping"; the
/// connection id is promoted to an attribute for <see cref="DeleteByConnectionAsync"/>.</summary>
public sealed class DynamoSsoDomainStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : ISsoDomainStore
{
    private const string MappingSk = "mapping";

    public async Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(domain.ToLowerInvariant()), MappingSk, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<SsoDomain>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SsoDomain>();
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, MappingSk);
        await foreach (var item in table.ScanAsync(filter, values, ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task UpsertAsync(SsoDomain domain, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(domain.Domain.ToLowerInvariant()), MappingSk);
        item.PutS("data", JsonSerializer.Serialize(domain, AwsJsonContext.Default.SsoDomain));
        item.PutS("connectionId", domain.ConnectionId);
        return table.PutAsync(item, ct);
    }

    public async Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        var pk = partitioner.PK(domain.ToLowerInvariant());
        var old = await table.DeleteIfExistsReturningAsync(pk, MappingSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("SsoDomains", pk, MappingSk, ct).ConfigureAwait(false);
    }

    public async Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, MappingSk);
        var v = new Dictionary<string, AttributeValue>(values) { [":c"] = new() { S = connectionId } };

        var keys = new List<(string, string)>();
        await foreach (var item in table.ScanAsync($"{filter} AND connectionId = :c", v, ct).ConfigureAwait(false))
        {
            var pk = item.GetStr(Dyn.Pk);
            var sk = item.GetStr(Dyn.Sk);
            await table.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
            keys.Add((pk, sk));
        }
        if (tombstones is not null && keys.Count > 0) await tombstones.WriteBatchAsync("SsoDomains", keys, ct).ConfigureAwait(false);
    }

    private static SsoDomain Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.SsoDomain)!;
}
