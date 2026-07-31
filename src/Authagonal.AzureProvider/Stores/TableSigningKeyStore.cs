using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableSigningKeyStore(
    TableClient signingKeysTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null,
    IFieldCipher? fieldCipher = null) : ISigningKeyStore
{
    /// <summary>
    /// At-rest protection for the private key material, through the same seam the user and grant
    /// stores use. Passthrough when the host registers no cipher, which is the historical layout.
    /// </summary>
    /// <remarks>
    /// KeyMaterialJson holds the full JWK, private scalar included. Written in the clear, anyone who
    /// could read the primary data store could mint a token this server would sign for — every
    /// session, every scope, every user. Complete impersonation of the issuer, from read access to
    /// the same table the data it protects lives in.
    /// </remarks>
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private async Task<SigningKeyInfo> ToModelAsync(SigningKeyEntity entity, CancellationToken ct)
    {
        var model = entity.ToModel();
        // ResolveAsync passes legacy plaintext through unchanged, so keys written before the cipher
        // was configured keep loading.
        model.KeyMaterialJson = await _cipher.ResolveAsync(model.KeyMaterialJson ?? "", ct).ConfigureAwait(false);
        return model;
    }

    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var pk = partitioner.PK(SigningKeyEntity.SigningPartitionKey);
        var query = signingKeysTable.QueryAsync<SigningKeyEntity>(
            e => e.PartitionKey == pk && e.IsActive,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            return await ToModelAsync(entity, ct).ConfigureAwait(false);
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
            results.Add(await ToModelAsync(entity, ct).ConfigureAwait(false));
        }

        return results;
    }

    public async Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default)
    {
        var entity = SigningKeyEntity.FromModel(key);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        entity.KeyMaterialJson = await _cipher.ProtectAsync(key.KeyMaterialJson, ct).ConfigureAwait(false);
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
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("SigningKeys", pk, keyId, ct);
        try
        {
            await signingKeysTable.DeleteEntityAsync(pk, keyId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
