using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IOidcProviderStore"/>. pk = connectionId, sk = "config".</summary>
public sealed class DynamoOidcProviderStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : IOidcProviderStore
{
    private const string ConfigSk = "config";

    public async Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(connectionId), ConfigSk, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OidcProviderConfig>();
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, ConfigSk);
        await foreach (var item in table.ScanAsync(filter, values, ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(config.ConnectionId), ConfigSk);
        item.PutS("data", JsonSerializer.Serialize(config, AwsJsonContext.Default.OidcProviderConfig));
        return table.PutAsync(item, ct);
    }

    public async Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(connectionId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("OidcProviders", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static OidcProviderConfig Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.OidcProviderConfig)!;
}
