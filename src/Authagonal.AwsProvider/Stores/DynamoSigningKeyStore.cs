using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="ISigningKeyStore"/>. All keys share one partition ("signing"), sk = keyId — the
/// set is tiny (one active key plus a few recently-rotated public keys), so the active-key lookup uses a
/// cheap server-side filter on the <c>active</c> attribute. This is the store the federated cross-cloud
/// JWKS reads from: every key here is published, so a peer cloud's public keys validate after failover.
/// </summary>
public sealed class DynamoSigningKeyStore(
    DynamoTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : ISigningKeyStore
{
    private const string Partition = "signing";

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

    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        await foreach (var item in table.QueryAsync(
            partitioner.PK(Partition),
            filterExpression: "active = :t",
            values: new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } },
            consistentRead: true,
            ct: ct).ConfigureAwait(false))
        {
            return await ReadAsync(item, ct).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SigningKeyInfo>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), consistentRead: true, ct: ct).ConfigureAwait(false))
            results.Add(await ReadAsync(item, ct).ConfigureAwait(false));
        return results;
    }

    public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default) => Write(key, ct);

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), keyId, ct).ConfigureAwait(false);
        if (item is null) return;

        var key = await ReadAsync(item, ct).ConfigureAwait(false);
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

    private async Task Write(SigningKeyInfo key, CancellationToken ct)
    {
        var item = Dyn.Item(partitioner.PK(Partition), key.KeyId);
        item.PutS("algorithm", key.Algorithm);
        item.PutS("keyMaterialJson", await _cipher.ProtectAsync(key.KeyMaterialJson, ct).ConfigureAwait(false));
        item.PutBool("active", key.IsActive);
        item.PutDate("createdAt", key.CreatedAt);
        item.PutDate("expiresAt", key.ExpiresAt);
        await table.PutAsync(item, ct).ConfigureAwait(false);
    }

    private async Task<SigningKeyInfo> ReadAsync(Dictionary<string, AttributeValue> item, CancellationToken ct) => new()
    {
        KeyId = item.GetStr(Dyn.Sk),
        Algorithm = item.GetStr("algorithm"),
        // ResolveAsync passes legacy plaintext through unchanged, so keys written before the cipher
        // was configured keep loading.
        KeyMaterialJson = await _cipher.ResolveAsync(item.GetStr("keyMaterialJson") ?? "", ct).ConfigureAwait(false),
        IsActive = item.GetBool("active"),
        CreatedAt = item.GetDate("createdAt"),
        ExpiresAt = item.GetDate("expiresAt"),
    };
}
