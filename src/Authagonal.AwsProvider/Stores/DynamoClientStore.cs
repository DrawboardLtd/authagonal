using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IClientStore"/>. One item per client: pk = client_id, sk = "config", with the
/// whole <see cref="OAuthClient"/> stored as a JSON document attribute (no field is queried server-side).
/// </summary>
public sealed class DynamoClientStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : IClientStore
{
    private const string ConfigSk = "config";

    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(clientId), ConfigSk, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default)
    {
        // Clients are keyed by client_id (no shared partition), so listing all means a scan — exactly
        // what the Azure store does. In a sandbox env, bound the scan to this env's pk prefix.
        var results = new List<OAuthClient>();
        var (filter, values) = ConfigScanFilter(partitioner, ConfigSk);
        await foreach (var item in table.ScanAsync(filter, values, ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(client.ClientId), ConfigSk);
        item.PutS("data", JsonSerializer.Serialize(client, AwsJsonContext.Default.OAuthClient));
        return table.PutAsync(item, ct);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(clientId);
        var old = await table.DeleteIfExistsReturningAsync(pk, ConfigSk, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("Clients", pk, ConfigSk, ct).ConfigureAwait(false);
    }

    private static OAuthClient Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.OAuthClient)!;

    /// <summary>
    /// A scan filter selecting a single sk value, optionally bounded to the env's pk-prefix range.
    /// Shared by the config-style stores (one config row per natural key).
    /// </summary>
    internal static (string Filter, IReadOnlyDictionary<string, AttributeValue> Values) ConfigScanFilter(EnvPartitioner partitioner, string sk)
    {
        var range = partitioner.RangeForEnv();
        if (range is null)
        {
            return ("sk = :sk", new Dictionary<string, AttributeValue> { [":sk"] = new() { S = sk } });
        }

        return ("sk = :sk AND pk >= :lo AND pk < :hi", new Dictionary<string, AttributeValue>
        {
            [":sk"] = new() { S = sk },
            [":lo"] = new() { S = range.Value.Low },
            [":hi"] = new() { S = range.Value.High },
        });
    }
}
