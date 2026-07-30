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
public sealed class SqlSigningKeyStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : ISigningKeyStore
{
    private const string Partition = "signing";

    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var filter = SqlKeyFilter.Partition(partitioner.PK(Partition)).WithAttr("active", "true");
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
            return Read(row);
        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SigningKeyInfo>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results;
    }

    public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default) => Write(key, ct);

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), keyId, ct: ct).ConfigureAwait(false);
        if (row is null) return;

        var key = Read(row);
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

    private Task Write(SigningKeyInfo key, CancellationToken ct)
    {
        var row = new SqlRow(partitioner.PK(Partition), key.KeyId);
        row.PutS("algorithm", key.Algorithm);
        row.PutS("keyMaterialJson", key.KeyMaterialJson);
        row.PutBool("active", key.IsActive);
        row.PutDate("createdAt", key.CreatedAt);
        row.PutDate("expiresAt", key.ExpiresAt);
        return table.PutAsync(row, ct);
    }

    private static SigningKeyInfo Read(SqlRow row) => new()
    {
        KeyId = row.Sk,
        Algorithm = row.GetStr("algorithm"),
        KeyMaterialJson = row.GetStr("keyMaterialJson"),
        IsActive = row.GetBool("active"),
        CreatedAt = row.GetDate("createdAt"),
        ExpiresAt = row.GetDate("expiresAt"),
    };
}
