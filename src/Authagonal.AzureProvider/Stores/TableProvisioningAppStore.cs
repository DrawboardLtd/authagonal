using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableProvisioningAppStore(TableClient appsTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null, IFieldCipher? fieldCipher = null) : IProvisioningAppStore
{
    // ApiKey is the outbound Bearer credential the orchestrator sends to a provisioning app's callback —
    // reversible, so it's encrypted at rest (not hashed): encrypt on write, decrypt on read. Passthrough
    // when the cipher is the Null default (single-tenant / unconfigured hosts + legacy plaintext rows).
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;
    private readonly bool _encrypts = fieldCipher is not null;

    private async Task<string?> EncryptApiKeyAsync(string? v, CancellationToken ct)
        => string.IsNullOrEmpty(v) ? v : await _cipher.ProtectAsync(v, ct);

    private async Task<string?> DecryptApiKeyAsync(string? v, CancellationToken ct)
        => string.IsNullOrEmpty(v) ? v : await _cipher.ResolveAsync(v, ct);

    public async Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
    {
        try
        {
            var entity = (await appsTable.GetEntityAsync<ProvisioningAppEntity>(
                partitioner.PK(ProvisioningAppEntity.AppsPartition), appId, cancellationToken: ct)).Value;
            entity.ApiKey = await DecryptApiKeyAsync(entity.ApiKey, ct);
            return entity.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(ProvisioningAppEntity.AppsPartition);
        var apps = new List<ProvisioningAppConfig>();
        await foreach (var entity in appsTable.QueryAsync<ProvisioningAppEntity>(
            e => e.PartitionKey == pk,
            cancellationToken: ct))
        {
            entity.ApiKey = await DecryptApiKeyAsync(entity.ApiKey, ct);
            apps.Add(entity.ToModel());
        }
        return apps;
    }

    public async Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        var entity = ProvisioningAppEntity.FromModel(app);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        entity.ApiKey = await EncryptApiKeyAsync(entity.ApiKey, ct);
        await appsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteUpsertAsync("ProvisioningApps", entity.PartitionKey, entity.RowKey, ct);
    }

    public async Task<int> MigrateProvisioningAppsAsync(bool dryRun, CancellationToken ct = default)
    {
        if (!_encrypts) return 0; // no cipher configured → plaintext IS the current scheme
        var pk = partitioner.PK(ProvisioningAppEntity.AppsPartition);
        var count = 0;
        await foreach (var e in appsTable.QueryAsync<ProvisioningAppEntity>(x => x.PartitionKey == pk, cancellationToken: ct))
        {
            if (string.IsNullOrEmpty(e.ApiKey)) continue;
            // Legacy iff Resolve leaves the value unchanged (i.e. it wasn't ciphertext). An already-encrypted
            // value decrypts to something different → skip. Uses the cipher's own semantics, no envelope
            // prefix hardcoded here.
            if (await _cipher.ResolveAsync(e.ApiKey, ct) != e.ApiKey) continue;
            count++;
            if (dryRun) continue;
            e.ApiKey = await _cipher.ProtectAsync(e.ApiKey, ct);
            await appsTable.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteUpsertAsync("ProvisioningApps", e.PartitionKey, e.RowKey, ct);
        }
        return count;
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(ProvisioningAppEntity.AppsPartition);
        try
        {
            await appsTable.DeleteEntityAsync(pk, appId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("ProvisioningApps", pk, appId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
