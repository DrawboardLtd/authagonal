using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IProvisioningAppStore"/>. All apps share one partition ("app"), sk = appId.</summary>
public sealed class DynamoProvisioningAppStore(DynamoTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IProvisioningAppStore
{
    private const string Partition = "app";

    public async Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), appId, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var apps = new List<ProvisioningAppConfig>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            apps.Add(Read(item));
        return apps;
    }

    public Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(Partition), app.AppId);
        item.PutS("data", JsonSerializer.Serialize(app, AwsJsonContext.Default.ProvisioningAppConfig));
        return table.PutAsync(item, ct);
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, appId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("ProvisioningApps", pk, appId, ct).ConfigureAwait(false);
    }

    private static ProvisioningAppConfig Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.ProvisioningAppConfig)!;
}
