using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="ISamlProviderStore"/>. pk = connectionId, sk = "config".</summary>
public sealed class DynamoSamlProviderStore(DynamoTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : ISamlProviderStore
{
    private const string ConfigSk = "config";

    public async Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(connectionId), ConfigSk, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SamlProviderConfig>();
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, ConfigSk);
        await foreach (var item in table.ScanAsync(filter, values, ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(config.ConnectionId), ConfigSk);
        item.PutS("data", JsonSerializer.Serialize(config, AwsJsonContext.Default.SamlProviderConfig));
        return table.PutAsync(item, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("SamlProviders", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static SamlProviderConfig Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.SamlProviderConfig)!;
}
