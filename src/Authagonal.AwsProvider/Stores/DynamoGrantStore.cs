using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IGrantStore"/>. Mirrors the Azure store's three-table layout — primary, a
/// by-subject index, and a by-expiry index — with the grant body kept as a JSON document attribute.
///
/// Two AWS-specific changes carry the safety-critical semantics:
/// <list type="bullet">
/// <item><b>Single-use redemption</b> (<see cref="TryConsumeAsync"/>) uses a conditional DeleteItem
/// (<c>attribute_exists(pk)</c>, ReturnValues=ALL_OLD) instead of Azure's ETag/If-Match delete. Exactly
/// one concurrent caller gets the row back; the rest see it gone. Same anti-replay guarantee.</item>
/// <item><b>The expiry index is re-keyed</b> as pk = "exp_{shard}", sk = "{yyyy-MM-dd}#{hashedKey}".
/// DynamoDB can't range-query a partition key, so the cleanup sweep range-scans the sort key
/// (<c>sk &lt;= "{cutoffDay}#~"</c>) across the fixed shards instead.</item>
/// </list>
/// </summary>
public sealed class DynamoGrantStore(
    DynamoTable grants,
    DynamoTable grantsBySubject,
    DynamoTable grantsByExpiry,
    EnvPartitioner partitioner,
    ILogger<DynamoGrantStore> logger,
    IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : IGrantStore
{
    private const string GrantSk = "grant";
    private const int ShardCount = 4; // matches GrantByExpiryEntity.ShardCount

    // Encrypts the serialized grant at rest. Unlike the Azure store (separate columns), the whole
    // PersistedGrant — including the raw token handle (Key) and the OidcSubject-bearing Data — is one
    // JSON attribute here, so encrypting the blob covers both: a table dump yields no live handles and
    // no session PII. Handle stays available in-memory after decrypt (RemoveBySubject re-hashes it).
    // Defaults to passthrough so OSS hosts are unchanged; Cloud injects the per-tenant Vault cipher.
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private Task<string> ProtectAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ProtectAsync(data, ct);

    private Task<string> ResolveAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ResolveAsync(data, ct);

    public async Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        var hashedKey = HashKey(grant.Key);
        var json = await ProtectAsync(JsonSerializer.Serialize(grant, AwsJsonContext.Default.PersistedGrant), ct).ConfigureAwait(false);

        // Primary write is the critical one.
        var primary = Dyn.Item(partitioner.PK(hashedKey), GrantSk);
        primary.PutS("data", json);
        await grants.PutAsync(primary, ct).ConfigureAwait(false);

        // Subject index — best-effort, but on failure compensate by deleting the primary so we never
        // leave an orphan that GetBySubject/Remove can't see.
        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var subject = Dyn.Item(partitioner.PK(grant.SubjectId), SubjectSk(grant.Type, hashedKey));
            subject.PutS("data", json);
            subject.PutS("clientId", grant.ClientId);
            try
            {
                await grantsBySubject.PutAsync(subject, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed subject index for grant {HashedKey}; compensating by deleting primary", hashedKey);
                try { await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false); }
                catch (Exception cex) when (cex is not OperationCanceledException)
                {
                    logger.LogCritical(cex, "CRITICAL: orphaned primary grant {HashedKey} after subject-index failure", hashedKey);
                }
                throw;
            }
        }

        // Expiry index — best-effort; reconciliation covers any miss.
        try
        {
            var expiry = Dyn.Item(partitioner.PK(ExpiryPk(Shard(hashedKey))), ExpirySk(grant.ExpiresAt, hashedKey));
            expiry.PutS("hashedKey", hashedKey);
            expiry.PutS("subjectId", grant.SubjectId);
            expiry.PutS("type", grant.Type);
            expiry.PutDate("expiresAt", grant.ExpiresAt);
            await grantsByExpiry.PutAsync(expiry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed expiry index for grant {HashedKey}; reconciliation will clean up", hashedKey);
        }
    }

    public async Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default)
    {
        var item = await grants.GetAsync(partitioner.PK(HashKey(key)), GrantSk, ct).ConfigureAwait(false);
        return item is null ? null : await ReadGrantAsync(item, ct).ConfigureAwait(false);
    }

    public async Task ConsumeAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var pk = partitioner.PK(hashedKey);

        var item = await grants.GetAsync(pk, GrantSk, ct).ConfigureAwait(false);
        if (item is null) return;

        var grant = await ReadGrantAsync(item, ct).ConfigureAwait(false);
        grant.ConsumedAt = DateTimeOffset.UtcNow;
        var json = await ProtectAsync(JsonSerializer.Serialize(grant, AwsJsonContext.Default.PersistedGrant), ct).ConfigureAwait(false);

        var updated = Dyn.Item(pk, GrantSk);
        updated.PutS("data", json);
        await grants.PutAsync(updated, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var spk = partitioner.PK(grant.SubjectId);
            var ssk = SubjectSk(grant.Type, hashedKey);
            if (await grantsBySubject.GetAsync(spk, ssk, ct).ConfigureAwait(false) is not null)
            {
                var s = Dyn.Item(spk, ssk);
                s.PutS("data", json);
                s.PutS("clientId", grant.ClientId);
                await grantsBySubject.PutAsync(s, ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("Subject index entry missing during consume for subject {SubjectId}, key {HashedKey}", grant.SubjectId, hashedKey);
            }
        }
    }

    public async Task<bool> TryConsumeAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var pk = partitioner.PK(hashedKey);

        // Atomic single-use: only the caller whose conditional delete removes the row wins; a racing
        // redemption gets null and loses.
        var old = await grants.DeleteIfExistsReturningAsync(pk, GrantSk, ct).ConfigureAwait(false);
        if (old is null) return false;

        var grant = await ReadGrantAsync(old, ct).ConfigureAwait(false);
        await CleanupIndexesAsync(hashedKey, grant.SubjectId, grant.Type, grant.ExpiresAt, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("Grants", pk, GrantSk, ct).ConfigureAwait(false);
        return true;
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var pk = partitioner.PK(hashedKey);

        var old = await grants.DeleteIfExistsReturningAsync(pk, GrantSk, ct).ConfigureAwait(false);
        if (old is null) return;

        var grant = await ReadGrantAsync(old, ct).ConfigureAwait(false);
        await CleanupIndexesAsync(hashedKey, grant.SubjectId, grant.Type, grant.ExpiresAt, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("Grants", pk, GrantSk, ct).ConfigureAwait(false);
    }

    public Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default)
        => RemoveBySubjectAsync(subjectId, clientId: null, ct);

    public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
        => RemoveBySubjectAsync(subjectId, clientId, ct);

    public async Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var results = new List<PersistedGrant>();
        await foreach (var item in grantsBySubject.QueryAsync(partitioner.PK(subjectId), ct: ct).ConfigureAwait(false))
            results.Add(await ReadGrantAsync(item, ct).ConfigureAwait(false));
        return results;
    }

    public async Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // sk = "{yyyy-MM-dd}#{hashedKey}", so "sk <= {cutoffDay}#~" captures every bucket up to and
        // including the cutoff day across each fixed shard. The cutoff-day bucket may hold not-yet-expired
        // entries, so re-check the stored expiresAt.
        var hi = $"{DateBucket(cutoff)}#~";
        for (var shard = 0; shard < ShardCount; shard++)
        {
            var epk = partitioner.PK(ExpiryPk(shard));
            var expired = new List<Dictionary<string, AttributeValue>>();
            await foreach (var item in grantsByExpiry.QueryAsync(
                epk,
                sortKeyCondition: "sk <= :hi",
                values: new Dictionary<string, AttributeValue> { [":hi"] = new() { S = hi } },
                ct: ct).ConfigureAwait(false))
            {
                if (item.GetDate("expiresAt") <= cutoff) expired.Add(item);
            }

            foreach (var item in expired)
            {
                var hashedKey = item.GetStr("hashedKey");
                var subjectId = item.GetS("subjectId");
                var type = item.GetStr("type");

                await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(subjectId))
                    await grantsBySubject.DeleteAsync(partitioner.PK(subjectId), SubjectSk(type, hashedKey), ct).ConfigureAwait(false);
                await grantsByExpiry.DeleteAsync(item.GetStr(Dyn.Pk), item.GetStr(Dyn.Sk), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveBySubjectAsync(string subjectId, string? clientId, CancellationToken ct)
    {
        var spk = partitioner.PK(subjectId);
        var filter = clientId is null ? null : "clientId = :c";
        var values = clientId is null ? null : new Dictionary<string, AttributeValue> { [":c"] = new() { S = clientId } };

        var items = new List<Dictionary<string, AttributeValue>>();
        await foreach (var item in grantsBySubject.QueryAsync(spk, filterExpression: filter, values: values, ct: ct).ConfigureAwait(false))
            items.Add(item);

        foreach (var item in items)
        {
            var grant = await ReadGrantAsync(item, ct).ConfigureAwait(false);
            var hashedKey = HashKey(grant.Key);

            await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
            await grantsBySubject.DeleteAsync(item.GetStr(Dyn.Pk), item.GetStr(Dyn.Sk), ct).ConfigureAwait(false);
            await grantsByExpiry.DeleteAsync(partitioner.PK(ExpiryPk(Shard(hashedKey))), ExpirySk(grant.ExpiresAt, hashedKey), ct).ConfigureAwait(false);

            if (tombstones is not null)
            {
                await tombstones.WriteAsync("Grants", partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                await tombstones.WriteAsync("GrantsBySubject", item.GetStr(Dyn.Pk), item.GetStr(Dyn.Sk), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupIndexesAsync(string hashedKey, string? subjectId, string type, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var expiryPk = partitioner.PK(ExpiryPk(Shard(hashedKey)));
        var expirySk = ExpirySk(expiresAt, hashedKey);
        await grantsByExpiry.DeleteAsync(expiryPk, expirySk, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(subjectId))
        {
            var spk = partitioner.PK(subjectId);
            var ssk = SubjectSk(type, hashedKey);
            await grantsBySubject.DeleteAsync(spk, ssk, ct).ConfigureAwait(false);
            if (tombstones is not null) await tombstones.WriteAsync("GrantsBySubject", spk, ssk, ct).ConfigureAwait(false);
        }

        if (tombstones is not null) await tombstones.WriteAsync("GrantsByExpiry", expiryPk, expirySk, ct).ConfigureAwait(false);
    }

    private async Task<PersistedGrant> ReadGrantAsync(Dictionary<string, AttributeValue> item, CancellationToken ct)
        => JsonSerializer.Deserialize(await ResolveAsync(item.GetStr("data"), ct).ConfigureAwait(false), AwsJsonContext.Default.PersistedGrant)!;

    public static string HashKey(string key)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string SubjectSk(string type, string hashedKey) => $"{type}|{hashedKey}";
    private static string ExpiryPk(int shard) => $"exp_{shard}";
    private static string ExpirySk(DateTimeOffset expiresAt, string hashedKey) => $"{DateBucket(expiresAt)}#{hashedKey}";
    private static string DateBucket(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd");

    private static int Shard(string hashedKey)
    {
        var c = hashedKey[0];
        var nibble = c <= '9' ? c - '0' : c - 'a' + 10;
        return nibble % ShardCount;
    }
}
