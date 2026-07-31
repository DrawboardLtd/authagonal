using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IMfaStore"/>. Three tables: credentials (pk = userId, sk = credentialId),
/// challenges (pk = challengeId, sk = "challenge"), and a WebAuthn credential-id index (pk = SHA256(id),
/// sk = "lookup"). Challenge consumption deletes-and-returns atomically to block replay.</summary>
public sealed class DynamoMfaStore(
    DynamoTable credentials,
    DynamoTable challenges,
    DynamoTable webAuthnIndex,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IMfaStore
{
    private const string ChallengeSk = "challenge";
    private const string Lookup = "lookup";

    public async Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<MfaCredential>();
        await foreach (var item in credentials.QueryAsync(partitioner.PK(userId), consistentRead: true, ct: ct).ConfigureAwait(false))
            results.Add(ReadCredential(item));
        return results;
    }

    public async Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var item = await credentials.GetAsync(partitioner.PK(userId), credentialId, ct).ConfigureAwait(false);
        return item is null ? null : ReadCredential(item);
    }

    public Task CreateCredentialAsync(MfaCredential credential, CancellationToken ct = default) => WriteCredential(credential, ct);
    public Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default) => WriteCredential(credential, ct);

    public async Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var old = await credentials.DeleteIfExistsReturningAsync(pk, credentialId, ct).ConfigureAwait(false);
        // Clear the WebAuthn credential-id index row so no stale lookup survives the delete.
        if (old is not null) await DeleteWebAuthnIndexForAsync(ReadCredential(old), ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("MfaCredentials", pk, credentialId, ct).ConfigureAwait(false);
    }

    public async Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var item in credentials.QueryAsync(pk, consistentRead: true, ct: ct).ConfigureAwait(false))
        {
            var sk = item.GetStr(Dyn.Sk);
            await DeleteWebAuthnIndexForAsync(ReadCredential(item), ct).ConfigureAwait(false);
            await credentials.DeleteAsync(pk, sk, ct).ConfigureAwait(false);
            keys.Add((pk, sk));
        }
        if (tombstones is not null && keys.Count > 0) await tombstones.WriteBatchAsync("MfaCredentials", keys, ct).ConfigureAwait(false);
    }

    public async Task<(string UserId, string CredentialId)?> FindByWebAuthnCredentialIdAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var item = await webAuthnIndex.GetAsync(partitioner.PK(Hash(webAuthnCredentialId)), Lookup, ct).ConfigureAwait(false);
        return item is null ? null : (item.GetStr("userId"), item.GetStr("credentialId"));
    }

    public Task StoreWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(Hash(webAuthnCredentialId)), Lookup);
        item.PutS("userId", userId);
        item.PutS("credentialId", credentialId);
        return webAuthnIndex.PutAsync(item, ct);
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
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("MfaWebAuthnIndex", pk, Lookup, ct).ConfigureAwait(false);
    }

    public Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(challenge.ChallengeId), ChallengeSk);
        item.PutS("data", JsonSerializer.Serialize(challenge, AwsJsonContext.Default.MfaChallenge));
        // A challenge is dead the moment it expires — GetChallengeAsync already refuses it — and
        // nothing ever removed the row. A day's margin covers clock skew and DynamoDB's best-effort
        // deletion window.
        item.PutTtl(challenge.ExpiresAt.AddDays(1));
        return challenges.PutAsync(item, ct);
    }

    public async Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        var item = await challenges.GetAsync(partitioner.PK(challengeId), ChallengeSk, ct).ConfigureAwait(false);
        if (item is null) return null;
        var challenge = ReadChallenge(item);
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
        var item = Dyn.Item(partitioner.PK(credential.UserId), credential.Id);
        item.PutS("data", JsonSerializer.Serialize(credential, AwsJsonContext.Default.MfaCredential));
        return credentials.PutAsync(item, ct);
    }

    private static string Hash(byte[] credentialId) => Convert.ToHexStringLower(SHA256.HashData(credentialId));

    private static MfaCredential ReadCredential(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.MfaCredential)!;

    private static MfaChallenge ReadChallenge(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.MfaChallenge)!;
}
