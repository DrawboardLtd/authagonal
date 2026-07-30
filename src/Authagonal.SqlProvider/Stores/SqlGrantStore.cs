using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IGrantStore"/>. Mirrors the other backends' three-table layout — primary, a
/// by-subject index, and a by-expiry index — with the grant body kept as a JSON document.
///
/// <list type="bullet">
/// <item><b>Single-use redemption</b> (<see cref="TryConsumeAsync"/>) is <c>DELETE … RETURNING</c>:
/// exactly one concurrent caller gets the row back, the same anti-replay guarantee as Azure's
/// ETag delete and DynamoDB's conditional delete.</item>
/// <item><b>Rotation</b> (<see cref="TryMarkConsumedAsync"/>) is an <c>UPDATE … WHERE consumedAt IS
/// NULL</c>: the one caller whose update affects a row wins the transition, and the loser must treat
/// its grant as replay / grace-window reuse.</item>
/// <item><b>The expiry index</b> is keyed pk = "exp_{shard}", sk = "{yyyy-MM-dd}#{hashedKey}", so the
/// cleanup sweep range-scans the sort key across the fixed shards. Those rows deliberately carry no
/// row-level TTL: reaping them independently would orphan the primary grants they point at, so
/// <see cref="RemoveExpiredAsync"/> stays the single owner of grant expiry.</item>
/// </list>
/// </summary>
public sealed class SqlGrantStore(
    SqlTable grants,
    SqlTable grantsBySubject,
    SqlTable grantsByExpiry,
    EnvPartitioner partitioner,
    ILogger<SqlGrantStore> logger,
    IChangeWriter? tombstones = null,
    IFieldCipher? fieldCipher = null) : IGrantStore
{
    private const string GrantSk = "grant";
    private const int ShardCount = 4; // matches GrantByExpiryEntity.ShardCount

    // Encrypts the serialized grant at rest. The whole PersistedGrant — including the raw token handle
    // (Key) and the OidcSubject-bearing Data — is one document here, so encrypting it covers both: a
    // table dump yields no live handles and no session PII. Defaults to passthrough so OSS hosts are
    // unchanged; Cloud injects the per-tenant Vault cipher.
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private Task<string> ProtectAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ProtectAsync(data, ct);

    private Task<string> ResolveAsync(string data, CancellationToken ct)
        => string.IsNullOrEmpty(data) ? Task.FromResult(data) : _cipher.ResolveAsync(data, ct);

    public async Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        var hashedKey = HashKey(grant.Key);
        var json = await ProtectAsync(JsonSerializer.Serialize(grant, SqlJsonContext.Default.PersistedGrant), ct).ConfigureAwait(false);

        // Primary write is the critical one.
        await grants.PutAsync(new SqlRow(partitioner.PK(hashedKey), GrantSk) { Data = json }, ct).ConfigureAwait(false);

        // Subject index — best-effort, but on failure compensate by deleting the primary so we never
        // leave an orphan that GetBySubject/Remove can't see.
        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var subject = new SqlRow(partitioner.PK(grant.SubjectId), SubjectSk(grant.Type, hashedKey)) { Data = json };
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
            var expiry = new SqlRow(partitioner.PK(ExpiryPk(Shard(hashedKey))), ExpirySk(grant.ExpiresAt, hashedKey));
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
        var row = await grants.GetAsync(partitioner.PK(HashKey(key)), GrantSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : await ReadGrantAsync(row, ct).ConfigureAwait(false);
    }

    public async Task ConsumeAsync(string key, CancellationToken ct = default)
    {
        var hashedKey = HashKey(key);
        var pk = partitioner.PK(hashedKey);

        var row = await grants.GetAsync(pk, GrantSk, ct: ct).ConfigureAwait(false);
        if (row is null) return;

        var grant = await ReadGrantAsync(row, ct).ConfigureAwait(false);
        grant.ConsumedAt = DateTimeOffset.UtcNow;
        var json = await ProtectAsync(JsonSerializer.Serialize(grant, SqlJsonContext.Default.PersistedGrant), ct).ConfigureAwait(false);

        await grants.PutAsync(new SqlRow(pk, GrantSk) { Data = json }, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var spk = partitioner.PK(grant.SubjectId);
            var ssk = SubjectSk(grant.Type, hashedKey);
            if (await grantsBySubject.GetAsync(spk, ssk, includeData: false, ct).ConfigureAwait(false) is not null)
            {
                var s = new SqlRow(spk, ssk) { Data = json };
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

        // Atomic single-use: only the caller whose delete removes the row wins; a racing redemption
        // gets nothing back and loses.
        var old = await grants.DeleteIfExistsReturningAsync(pk, GrantSk, ct).ConfigureAwait(false);
        if (old is null) return false;

        var grant = await ReadGrantAsync(old, ct).ConfigureAwait(false);
        await CleanupIndexesAsync(hashedKey, grant.SubjectId, grant.Type, grant.ExpiresAt, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("Grants", pk, GrantSk, ct).ConfigureAwait(false);
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
        var json = await ProtectAsync(JsonSerializer.Serialize(grant, SqlJsonContext.Default.PersistedGrant), ct).ConfigureAwait(false);

        var row = new SqlRow(pk, GrantSk) { Data = json };
        // Top-level guard marker (the ConsumedAt inside the encrypted document can't gate a
        // condition). Its presence is what a racing consume trips on.
        row.PutDate("consumedAt", grant.ConsumedAt.Value);

        // Atomic compare-and-set: land the consumed marker only if the row exists and is NOT already
        // consumed. A concurrent consume wins first → this update matches nothing → the caller loses
        // and must re-evaluate as replay.
        if (!await grants.UpdateIfAttrNullAsync(row, "consumedAt", ct).ConfigureAwait(false))
            return false;

        // Mirror the consumed marker to the subject index (best-effort).
        if (!string.IsNullOrEmpty(grant.SubjectId))
        {
            var spk = partitioner.PK(grant.SubjectId);
            var ssk = SubjectSk(grant.Type, hashedKey);
            if (await grantsBySubject.GetAsync(spk, ssk, includeData: false, ct).ConfigureAwait(false) is not null)
            {
                var s = new SqlRow(spk, ssk) { Data = json };
                s.PutS("clientId", grant.ClientId);
                s.PutDate("consumedAt", grant.ConsumedAt.Value);
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
        => RemoveBySubjectAsync(subjectId, clientId: null, ct);

    public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
        => RemoveBySubjectAsync(subjectId, clientId, ct);

    public async Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var results = new List<PersistedGrant>();
        await foreach (var row in grantsBySubject.QueryPartitionAsync(partitioner.PK(subjectId), ct).ConfigureAwait(false))
            results.Add(await ReadGrantAsync(row, ct).ConfigureAwait(false));
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
            var expired = new List<SqlRow>();
            var filter = SqlKeyFilter.Partition(epk) with { SkAtMost = hi };
            await foreach (var row in grantsByExpiry.QueryAsync(filter, ct).ConfigureAwait(false))
            {
                if (row.GetDate("expiresAt") <= cutoff) expired.Add(row);
            }

            foreach (var row in expired)
            {
                var hashedKey = row.GetStr("hashedKey");
                var subjectId = row.GetS("subjectId");
                var type = row.GetStr("type");

                // Tombstone-first (F24e contract) — record every delete before removing the row so an
                // incremental backup can't miss an expired-grant deletion.
                if (tombstones is not null)
                {
                    await tombstones.WriteAsync("Grants", partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(subjectId))
                        await tombstones.WriteAsync("GrantsBySubject", partitioner.PK(subjectId), SubjectSk(type, hashedKey), ct).ConfigureAwait(false);
                    await tombstones.WriteAsync("GrantsByExpiry", row.Pk, row.Sk, ct).ConfigureAwait(false);
                }

                await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(subjectId))
                    await grantsBySubject.DeleteAsync(partitioner.PK(subjectId), SubjectSk(type, hashedKey), ct).ConfigureAwait(false);
                await grantsByExpiry.DeleteAsync(row.Pk, row.Sk, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveBySubjectAsync(string subjectId, string? clientId, CancellationToken ct)
    {
        var spk = partitioner.PK(subjectId);
        var filter = SqlKeyFilter.Partition(spk);
        if (clientId is not null) filter = filter.WithAttr("clientId", clientId);

        var rows = new List<SqlRow>();
        await foreach (var row in grantsBySubject.QueryAsync(filter, ct).ConfigureAwait(false))
            rows.Add(row);

        foreach (var row in rows)
        {
            var grant = await ReadGrantAsync(row, ct).ConfigureAwait(false);
            var hashedKey = HashKey(grant.Key);

            await grants.DeleteAsync(partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
            await grantsBySubject.DeleteAsync(row.Pk, row.Sk, ct).ConfigureAwait(false);
            await grantsByExpiry.DeleteAsync(partitioner.PK(ExpiryPk(Shard(hashedKey))), ExpirySk(grant.ExpiresAt, hashedKey), ct).ConfigureAwait(false);

            if (tombstones is not null)
            {
                await tombstones.WriteAsync("Grants", partitioner.PK(hashedKey), GrantSk, ct).ConfigureAwait(false);
                await tombstones.WriteAsync("GrantsBySubject", row.Pk, row.Sk, ct).ConfigureAwait(false);
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

    private async Task<PersistedGrant> ReadGrantAsync(SqlRow row, CancellationToken ct)
        => JsonSerializer.Deserialize(
            await ResolveAsync(row.DataOrEmpty, ct).ConfigureAwait(false), SqlJsonContext.Default.PersistedGrant)!;

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
