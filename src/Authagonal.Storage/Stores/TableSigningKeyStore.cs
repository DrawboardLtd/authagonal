using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableSigningKeyStore(TableClient signingKeysTable) : ISigningKeyStore
{
    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var query = signingKeysTable.QueryAsync<SigningKeyEntity>(
            e => e.PartitionKey == SigningKeyEntity.SigningPartitionKey && e.IsActive,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            return entity.ToModel();
        }

        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllKeysAsync(CancellationToken ct = default)
    {
        var results = new List<SigningKeyInfo>();
        var query = signingKeysTable.QueryAsync<SigningKeyEntity>(
            e => e.PartitionKey == SigningKeyEntity.SigningPartitionKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task StoreKeyAsync(SigningKeyInfo key, CancellationToken ct = default)
    {
        var entity = SigningKeyEntity.FromModel(key);
        await signingKeysTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        try
        {
            var response = await signingKeysTable.GetEntityAsync<SigningKeyEntity>(
                SigningKeyEntity.SigningPartitionKey, keyId, cancellationToken: ct);

            var entity = response.Value;
            entity.IsActive = false;
            await signingKeysTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
    {
        try
        {
            await signingKeysTable.DeleteEntityAsync(
                SigningKeyEntity.SigningPartitionKey, keyId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
