using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IMfaStore"/>. Three tables: credentials (pk = userId, sk = credentialId), challenges
/// (pk = challengeId, sk = "challenge"), and a WebAuthn credential-id index (pk = SHA256(id),
/// sk = "lookup"). Challenge consumption deletes-and-returns atomically to block replay, and a
/// challenge row carries its own expiry so the reaper clears the ones never redeemed.
/// </summary>
public sealed class SqlMfaStore(
    SqlTable credentials,
    SqlTable challenges,
    SqlTable webAuthnIndex,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IMfaStore
{
    private const string ChallengeSk = "challenge";
    private const string Lookup = "lookup";

    public async Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<MfaCredential>();
        await foreach (var row in credentials.QueryPartitionAsync(partitioner.PK(userId), ct).ConfigureAwait(false))
            results.Add(ReadCredential(row));
        return results;
    }

    public async Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var row = await credentials.GetAsync(partitioner.PK(userId), credentialId, ct: ct).ConfigureAwait(false);
        return row is null ? null : ReadCredential(row);
    }

    public Task CreateCredentialAsync(MfaCredential credential, CancellationToken ct = default) => WriteCredential(credential, ct);
    public Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default) => WriteCredential(credential, ct);

    public async Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var old = await credentials.DeleteIfExistsReturningAsync(pk, credentialId, ct).ConfigureAwait(false);
        // Clear the WebAuthn credential-id index row so no stale lookup survives the delete.
        if (old is not null) await DeleteWebAuthnIndexForAsync(ReadCredential(old), ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("MfaCredentials", pk, credentialId, ct).ConfigureAwait(false);
    }

    public async Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var row in credentials.QueryPartitionAsync(pk, ct).ConfigureAwait(false))
        {
            await DeleteWebAuthnIndexForAsync(ReadCredential(row), ct).ConfigureAwait(false);
            await credentials.DeleteAsync(pk, row.Sk, ct).ConfigureAwait(false);
            keys.Add((pk, row.Sk));
        }
        if (tombstones is not null && keys.Count > 0)
            await tombstones.WriteBatchAsync("MfaCredentials", keys, ct).ConfigureAwait(false);
    }

    public async Task<(string UserId, string CredentialId)?> FindByWebAuthnCredentialIdAsync(
        byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var row = await webAuthnIndex.GetAsync(partitioner.PK(Hash(webAuthnCredentialId)), Lookup, ct: ct).ConfigureAwait(false);
        return row is null ? null : (row.GetStr("userId"), row.GetStr("credentialId"));
    }

    public Task<bool> TryStoreWebAuthnCredentialIdMappingAsync(
        byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(Hash(webAuthnCredentialId)), Lookup);
        row.PutS("userId", userId);
        row.PutS("credentialId", credentialId);
        // INSERT … ON CONFLICT DO NOTHING — the claim is the write, so a second registration of the same
        // credential id loses rather than repointing the index at itself.
        return webAuthnIndex.PutIfAbsentAsync(row, ct);
    }

    // Delete the WebAuthn credential-id index row for a credential (no-op for TOTP/recovery or missing
    // data). No try/catch: TryGetWebAuthnCredentialId absorbs malformed JSON/base64 (returns false), and
    // the storage delete is left to PROPAGATE a transient fault rather than silently leave the stale row.
    private async Task DeleteWebAuthnIndexForAsync(MfaCredential credential, CancellationToken ct)
    {
        if (credential.TryGetWebAuthnCredentialId(out var credentialId))
            await DeleteWebAuthnCredentialIdMappingAsync(credentialId, ct).ConfigureAwait(false);
    }

    public async Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Hash(webAuthnCredentialId));
        var old = await webAuthnIndex.DeleteIfExistsReturningAsync(pk, Lookup, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("MfaWebAuthnIndex", pk, Lookup, ct).ConfigureAwait(false);
    }

    public Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(challenge.ChallengeId), ChallengeSk)
        {
            Data = JsonSerializer.Serialize(challenge, SqlJsonContext.Default.MfaChallenge),
            ExpiresAt = challenge.ExpiresAt,
        };
        return challenges.PutAsync(row, ct);
    }

    public async Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        var row = await challenges.GetAsync(partitioner.PK(challengeId), ChallengeSk, ct: ct).ConfigureAwait(false);
        if (row is null) return null;
        var challenge = ReadChallenge(row);
        return challenge.IsConsumed || challenge.ExpiresAt <= DateTimeOffset.UtcNow ? null : challenge;
    }

    public async Task<MfaChallenge?> ConsumeChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        // Delete-and-return atomically so a challenge can be consumed exactly once (replay-safe).
        var old = await challenges.DeleteIfExistsReturningAsync(partitioner.PK(challengeId), ChallengeSk, ct).ConfigureAwait(false);
        if (old is null) return null;
        var challenge = ReadChallenge(old);
        return challenge.IsConsumed || challenge.ExpiresAt <= DateTimeOffset.UtcNow ? null : challenge;
    }

    private Task WriteCredential(MfaCredential credential, CancellationToken ct)
    {
        var row = new SqlRow(partitioner.PK(credential.UserId), credential.Id)
        {
            Data = JsonSerializer.Serialize(credential, SqlJsonContext.Default.MfaCredential),
        };
        return credentials.PutAsync(row, ct);
    }

    private static string Hash(byte[] credentialId) => Convert.ToHexStringLower(SHA256.HashData(credentialId));

    private static MfaCredential ReadCredential(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.MfaCredential)!;

    private static MfaChallenge ReadChallenge(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.MfaChallenge)!;
}
