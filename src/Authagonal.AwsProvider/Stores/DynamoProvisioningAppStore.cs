using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IProvisioningAppStore"/>. All apps share one partition ("app"), sk = appId.
/// The serialized config document (which carries the app's API key) is encrypted at rest via the
/// optional <see cref="IFieldCipher"/> — passthrough by default, legacy plaintext rows resolve
/// unchanged (the same seam contract as the user store's document crypto).
/// </summary>
public sealed class DynamoProvisioningAppStore(
    DynamoTable table,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : IProvisioningAppStore
{
    private const string Partition = "app";

    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    public async Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), appId, ct).ConfigureAwait(false);
        return item is null ? null : await ReadAsync(item, ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Strongly consistent, because the provisioning-app quota is enforced by counting through here
    /// immediately after a write (<c>ProvisioningEndpoints.CreateApp</c>). Under the default eventually
    /// consistent read, two concurrent creates can each write and each re-count against a replica that
    /// has seen neither, so both keep their rows and a paid cap is exceeded — the exact race the
    /// re-count exists to close. The other correctness-critical reads in this provider
    /// (<c>DynamoSigningKeyStore</c>, <c>DynamoGrantStore</c>, <c>DynamoMfaStore</c>) opt in for the
    /// same reason.
    /// </remarks>
    public async Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var apps = new List<ProvisioningAppConfig>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), consistentRead: true, ct: ct).ConfigureAwait(false))
            apps.Add(await ReadAsync(item, ct).ConfigureAwait(false));
        return apps;
    }

    public async Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(Partition), app.AppId);
        var json = JsonSerializer.Serialize(app, AwsJsonContext.Default.ProvisioningAppConfig);
        item.PutS("data", await _cipher.ProtectAsync(json, ct).ConfigureAwait(false));
        await table.PutAsync(item, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, appId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("ProvisioningApps", pk, appId, ct).ConfigureAwait(false);
    }

    private async Task<ProvisioningAppConfig> ReadAsync(Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var json = await _cipher.ResolveAsync(item.GetStr("data"), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, AwsJsonContext.Default.ProvisioningAppConfig)!;
    }
}
