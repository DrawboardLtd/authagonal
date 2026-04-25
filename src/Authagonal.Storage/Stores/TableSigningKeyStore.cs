using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableSigningKeyStore(TableClient signingKeysTable, EnvPartitioner partitioner, ITombstoneWriter? tombstoneWriter = null) : ISigningKeyStore
{
    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(SigningKeyEntity.SigningPartitionKey);
        var query = signingKeysTable.QueryAsync<SigningKeyEntity>(
            e => e.PartitionKey == pk && e.IsActive,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            return entity.ToModel();
        }

        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(SigningKeyEntity.SigningPartitionKey);
        var results = new List<SigningKeyInfo>();
        var query = signingKeysTable.QueryAsync<SigningKeyEntity>(
            e => e.PartitionKey == pk,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default)
    {
        var entity = SigningKeyEntity.FromModel(key);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await signingKeysTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(SigningKeyEntity.SigningPartitionKey);
        try
        {
            var response = await signingKeysTable.GetEntityAsync<SigningKeyEntity>(
                pk, keyId, cancellationToken: ct);

            var entity = response.Value;
            entity.IsActive = false;
            await signingKeysTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task DeleteAsync(string keyId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(SigningKeyEntity.SigningPartitionKey);
        try
        {
            await signingKeysTable.DeleteEntityAsync(pk, keyId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("SigningKeys", pk, keyId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
