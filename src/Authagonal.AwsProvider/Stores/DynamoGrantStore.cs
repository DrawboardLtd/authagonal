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
        // Grants read back from storage carry no Key. Re-storing one without setting it would hash
        // the empty string and write to the SHA-256("") partition instead of the real row — silently
        // corrupting the grant rather than updating it.
        if (string.IsNullOrEmpty(grant.Key))
            throw new ArgumentException(
                "PersistedGrant.Key is empty. Grants read back from storage have no Key — set it explicitly before storing.",
                nameof(grant));

        var hashedKey = HashKey(grant.Key);
        var json = await ProtectAsync(SerializeWithoutHandle(grant), ct).ConfigureAwait(false);

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
        var json = await ProtectAsync(SerializeWithoutHandle(grant), ct).ConfigureAwait(false);

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

    public async Task<bool> TryUpdateDataIfUnconsumedAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(grant.Key))
            throw new ArgumentException(
                "PersistedGrant.Key is empty. Grants read back from storage have no Key — set it explicitly before updating.",
                nameof(grant));

        var hashedKey = HashKey(grant.Key);
        var pk = partitioner.PK(hashedKey);
        var json = await ProtectAsync(SerializeWithoutHandle(grant), ct).ConfigureAwait(false);

        // No consumedAt attribute: the condition requires its absence, and this row must stay un-consumed.
        // The grants item carries pk/sk/data only (see StoreAsync), so a full Put drops nothing.
        var item = Dyn.Item(pk, GrantSk);
        item.PutS("data", json);

        // Same condition as the consume-mark: the row must exist and must not be consumed. A concurrent
        // consume or delete fails the condition and this caller loses.
        try
        {
            await grants.Client.PutItemAsync(new PutItemRequest
            {
                TableName = grants.Name,
                Item = item,
                ConditionExpression = "attribute_exists(pk) AND attribute_not_exists(consumedAt)",
            }, ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        // Mirror to the subject index, best-effort.
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
        }

        return true;
    }

    public async Task<bool> TryMarkConsumedAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(grant.Key))
            throw new ArgumentException(
                "PersistedGrant.Key is empty. Grants read back from storage have no Key — set it explicitly before marking consumed.",
                nameof(grant));

        var hashedKey = HashKey(grant.Key);
        var pk = partitioner.PK(hashedKey);

        grant.ConsumedAt ??= DateTimeOffset.UtcNow;
        var json = await ProtectAsync(SerializeWithoutHandle(grant), ct).ConfigureAwait(false);

        var item = Dyn.Item(pk, GrantSk);
        item.PutS("data", json);
        // Top-level guard marker (the ConsumedAt inside the encrypted `data` blob can't gate a
        // condition). Its presence is what a racing consume trips on.
        item.PutS("consumedAt", grant.ConsumedAt.Value.ToString("O"));

        // Atomic compare-and-set: land the consumed marker only if the row exists and is NOT already
        // consumed. A concurrent consume wins first → this put's condition fails → the caller loses and
        // must re-evaluate as replay. DynamoDB analog of Azure's ETag-conditional update.
        try
        {
            await grants.Client.PutItemAsync(new PutItemRequest
            {
                TableName = grants.Name,
                Item = item,
                ConditionExpression = "attribute_exists(pk) AND attribute_not_exists(consumedAt)",
            }, ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        // Mirror the consumed marker to the subject index (best-effort).
        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var spk = partitioner.PK(grant.SubjectId);
            var ssk = SubjectSk(grant.Type, hashedKey);
            if (await grantsBySubject.GetAsync(spk, ssk, ct).ConfigureAwait(false) is not null)
            {
                var s = Dyn.Item(spk, ssk);
                s.PutS("data", json);
                s.PutS("clientId", grant.ClientId);
                s.PutS("consumedAt", grant.ConsumedAt.Value.ToString("O"));
                await grantsBySubject.PutAsync(s, ct).ConfigureAwait(false);
            }
        }

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
        => RemoveBySubjectCoreAsync(subjectId, clientId: null, types: null, ct);

    public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
        => RemoveBySubjectCoreAsync(subjectId, clientId, types: null, ct);

    public Task RemoveBySubjectAsync(
        string subjectId,
        IReadOnlyCollection<string> types,
        string? clientId = null,
        CancellationToken ct = default)
        => types.Count == 0
            ? Task.CompletedTask
            : RemoveBySubjectCoreAsync(subjectId, clientId, new HashSet<string>(types, StringComparer.Ordinal), ct);

    public async Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var results = new List<PersistedGrant>();
        await foreach (var item in grantsBySubject.QueryAsync(partitioner.PK(subjectId), consistentRead: true, ct: ct).ConfigureAwait(false))
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

                // Tombstone-first (F24e contract) — record every delete before removing the row so an
                // incremental backup can't miss an expired-grant deletion.
                if (tombstones is not null)
                {
                    await tombstones.WriteAsync("Grants", partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(subjectId))
                        await tombstones.WriteAsync("GrantsBySubject", partitioner.PK(subjectId), SubjectSk(type, hashedKey), ct).ConfigureAwait(false);
                    await tombstones.WriteAsync("GrantsByExpiry", item.GetStr(Dyn.Pk), item.GetStr(Dyn.Sk), ct).ConfigureAwait(false);
                }

                await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(subjectId))
                    await grantsBySubject.DeleteAsync(partitioner.PK(subjectId), SubjectSk(type, hashedKey), ct).ConfigureAwait(false);
                await grantsByExpiry.DeleteAsync(item.GetStr(Dyn.Pk), item.GetStr(Dyn.Sk), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Bulk subject removal. <paramref name="types"/> null means every type; otherwise only those types
    /// are removed (the index item carries <c>type</c>, so this costs no extra reads).
    /// </summary>
    private async Task RemoveBySubjectCoreAsync(
        string subjectId, string? clientId, HashSet<string>? types, CancellationToken ct)
    {
        var spk = partitioner.PK(subjectId);
        var filter = clientId is null ? null : "clientId = :c";
        var values = clientId is null ? null : new Dictionary<string, AttributeValue> { [":c"] = new() { S = clientId } };

        var items = new List<Dictionary<string, AttributeValue>>();
        await foreach (var item in grantsBySubject.QueryAsync(spk, filterExpression: filter, values: values, consistentRead: true, ct: ct).ConfigureAwait(false))
        {
            if (types is not null && !types.Contains(item.GetStr("type"))) continue;
            items.Add(item);
        }

        foreach (var item in items)
        {
            var grant = await ReadGrantAsync(item, ct).ConfigureAwait(false);
            // Taken from the index row's sort key ("{type}|{hashedKey}"), not by re-hashing
            // grant.Key — the raw handle is deliberately not persisted, so it is empty here.
            var hashedKey = HashedKeyFromSubjectSk(item.GetStr(Dyn.Sk));

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

    /// <summary>
    /// Serializes a grant with the raw token handle blanked.
    /// </summary>
    /// <remarks>
    /// PersistedGrant.Key IS the bearer credential — the refresh-token / authorization-code /
    /// device-code handle — and the whole object was being written into the data attribute, so both
    /// the primary row and the subject-index row held live, replayable tokens in the clear. Only the
    /// SHA-256 belongs in storage, which is what the partition key already carries. The Azure store
    /// has always done this and says why: "storing the handle would let a table dump replay live
    /// tokens." The class comment here claimed the same invariant, but it held only when a non-null
    /// IFieldCipher was injected — and the default is passthrough, which is every OSS and
    /// self-hosted host.
    /// <para>
    /// Blanked on a copy so a concurrent reader of the caller's object never observes an empty Key.
    /// Reads return Key empty too, matching Azure, so no caller can depend on one backend populating
    /// it and another not.
    /// </para>
    /// </remarks>
    private static string SerializeWithoutHandle(PersistedGrant grant) =>
        JsonSerializer.Serialize(new PersistedGrant
        {
            Key = string.Empty,
            Type = grant.Type,
            SubjectId = grant.SubjectId,
            ClientId = grant.ClientId,
            Data = grant.Data,
            CreatedAt = grant.CreatedAt,
            ExpiresAt = grant.ExpiresAt,
            ConsumedAt = grant.ConsumedAt,
        }, AwsJsonContext.Default.PersistedGrant);

    /// <summary>The hashed key embedded in a subject-index sort key.</summary>
    private static string HashedKeyFromSubjectSk(string sk)
    {
        var bar = sk.IndexOf('|');
        return bar >= 0 ? sk[(bar + 1)..] : sk;
    }

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
