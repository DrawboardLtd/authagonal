using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IProvisioningAppStore"/>. All apps share one partition ("app"), sk = appId. The
/// serialized config document (which carries the app's API key) is encrypted at rest via the optional
/// <see cref="IFieldCipher"/> — passthrough by default, and plaintext rows written before a cipher was
/// configured resolve unchanged, which is what makes turning encryption on a non-event.
/// </summary>
public sealed class SqlProvisioningAppStore(
    SqlTable table,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : IProvisioningAppStore
{
    private const string Partition = "app";

    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    public async Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), appId, ct: ct).ConfigureAwait(false);
        return row is null ? null : await ReadAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var apps = new List<ProvisioningAppConfig>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            apps.Add(await ReadAsync(row, ct).ConfigureAwait(false));
        return apps;
    }

    public async Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(app, SqlJsonContext.Default.ProvisioningAppConfig);
        var row = new SqlRow(partitioner.PK(Partition), app.AppId)
        {
            Data = await _cipher.ProtectAsync(json, ct).ConfigureAwait(false),
        };
        await table.PutAsync(row, ct).ConfigureAwait(false);
        // Recorded, not just deletes: an incremental window that carries the deletions and none of
        // the writes reconstructs a table that is missing every row created or changed in it.
        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("ProvisioningApps", partitioner.PK(Partition), app.AppId, ct)
                .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, appId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("ProvisioningApps", pk, appId, ct).ConfigureAwait(false);
    }

    private async Task<ProvisioningAppConfig> ReadAsync(SqlRow row, CancellationToken ct)
    {
        var json = await _cipher.ResolveAsync(row.DataOrEmpty, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, SqlJsonContext.Default.ProvisioningAppConfig)!;
    }
}
