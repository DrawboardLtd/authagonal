using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="ISigningKeyStore"/>. All keys share one partition ("signing"), sk = keyId — the set
/// is tiny (one active key plus a few recently-rotated public keys), so the active-key lookup filters
/// on the promoted <c>active</c> attribute. Every key here is published via JWKS.
/// </summary>
public sealed class SqlSigningKeyStore(
    SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : ISigningKeyStore
{
    private const string Partition = "signing";

    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var filter = SqlKeyFilter.Partition(partitioner.PK(Partition)).WithAttr("active", "true");
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
            return await ReadAsync(row, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SigningKeyInfo>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            results.Add(await ReadAsync(row, ct).ConfigureAwait(false));
        return results;
    }

    public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default) => Write(key, ct);

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), keyId, ct: ct).ConfigureAwait(false);
        if (row is null) return;

        var key = await ReadAsync(row, ct).ConfigureAwait(false);
        key.IsActive = false;
        await Write(key, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string keyId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, keyId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("SigningKeys", pk, keyId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// At-rest protection for the private key material, through the same seam the user and grant
    /// stores use. Passthrough when the host registers no cipher, which is the historical layout.
    /// </summary>
    /// <remarks>
    /// KeyMaterialJson holds the full JWK — including <c>d</c>, the private scalar. It was written in
    /// the clear, so anyone who could read the primary data store could mint a token this server
    /// would sign for: every session, every scope, every user. That is the one secret whose exposure
    /// is not degraded access but complete impersonation of the issuer, and it sat beside the data it
    /// protects. Every other credential-bearing column already went through this seam.
    /// </remarks>
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private async Task Write(SigningKeyInfo key, CancellationToken ct)
    {
        var row = new SqlRow(partitioner.PK(Partition), key.KeyId);
        row.PutS("algorithm", key.Algorithm);
        row.PutS("keyMaterialJson", await _cipher.ProtectAsync(key.KeyMaterialJson, ct).ConfigureAwait(false));
        row.PutBool("active", key.IsActive);
        row.PutDate("createdAt", key.CreatedAt);
        row.PutDate("expiresAt", key.ExpiresAt);
        await table.PutAsync(row, ct).ConfigureAwait(false);
    }

    private async Task<SigningKeyInfo> ReadAsync(SqlRow row, CancellationToken ct) => new()
    {
        KeyId = row.Sk,
        Algorithm = row.GetStr("algorithm"),
        // ResolveAsync passes an unrecognised (legacy plaintext) value through unchanged, so keys
        // written before the cipher was configured keep loading.
        KeyMaterialJson = await _cipher.ResolveAsync(row.GetStr("keyMaterialJson") ?? "", ct).ConfigureAwait(false),
        IsActive = row.GetBool("active"),
        CreatedAt = row.GetDate("createdAt"),
        ExpiresAt = row.GetDate("expiresAt"),
    };
}
