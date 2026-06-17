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
public sealed class DynamoSigningKeyStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : ISigningKeyStore
{
    private const string Partition = "signing";

    public async Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        await foreach (var item in table.QueryAsync(
            partitioner.PK(Partition),
            filterExpression: "active = :t",
            values: new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } },
            ct: ct).ConfigureAwait(false))
        {
            return Read(item);
        }

        return null;
    }

    public async Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<SigningKeyInfo>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            results.Add(Read(item));
        return results;
    }

    public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default) => Write(key, ct);

    public async Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), keyId, ct).ConfigureAwait(false);
        if (item is null) return;

        var key = Read(item);
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
        var item = Dyn.Item(partitioner.PK(Partition), key.KeyId);
        item.PutS("algorithm", key.Algorithm);
        item.PutS("keyMaterialJson", key.KeyMaterialJson);
        item.PutBool("active", key.IsActive);
        item.PutDate("createdAt", key.CreatedAt);
        item.PutDate("expiresAt", key.ExpiresAt);
        return table.PutAsync(item, ct);
    }

    private static SigningKeyInfo Read(Dictionary<string, AttributeValue> item) => new()
    {
        KeyId = item.GetStr(Dyn.Sk),
        Algorithm = item.GetStr("algorithm"),
        KeyMaterialJson = item.GetStr("keyMaterialJson"),
        IsActive = item.GetBool("active"),
        CreatedAt = item.GetDate("createdAt"),
        ExpiresAt = item.GetDate("expiresAt"),
    };
}
