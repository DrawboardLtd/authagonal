using System.Security.Cryptography;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableMfaStore(
    TableClient credentialsTable,
    TableClient challengesTable,
    TableClient webAuthnIndexTable,
    EnvPartitioner partitioner,
    IChangeWriter? tombstoneWriter = null) : IMfaStore
{
    public async Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var results = new List<MfaCredential>();
        await foreach (var entity in credentialsTable.QueryAsync<MfaCredentialEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            results.Add(entity.ToModel());
        }
        return results;
    }

    public async Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        try
        {
            var response = await credentialsTable.GetEntityAsync<MfaCredentialEntity>(
                partitioner.PK(userId), credentialId, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task CreateCredentialAsync(MfaCredential credential, CancellationToken ct = default)
    {
        var entity = MfaCredentialEntity.FromModel(credential);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await credentialsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default)
    {
        var entity = MfaCredentialEntity.FromModel(credential);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await credentialsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public Task<bool> TryClaimTotpStepAsync(string userId, string credentialId, long step, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, e => (e.LastTotpStep ?? long.MinValue) < step, e => e.LastTotpStep = step, ct);

    public Task<bool> TryConsumeRecoveryCodeAsync(string userId, string credentialId, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, e => !e.IsConsumed, e => e.IsConsumed = true, ct);

    /// <inheritdoc />
    public Task<bool> TryUpgradeRecoverySecretAsync(
        string userId, string credentialId, string secretProtected, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, e => !e.IsConsumed,
            e => e.SecretProtected = secretProtected, ct, touchLastUsed: false);

    /// <summary>
    /// Re-reads the entity, re-tests the guard against what is actually stored, and writes back under
    /// the ETag that read returned — so the transition happens for exactly one caller.
    /// </summary>
    /// <remarks>
    /// A 412 is reported as a lost claim rather than retried. Contention on a single MFA credential row
    /// means two requests are verifying the same credential at the same instant, which is the case this
    /// exists to refuse; failing closed is the right answer for a second factor either way.
    /// </remarks>
    private async Task<bool> ClaimAsync(
        string userId, string credentialId,
        Func<MfaCredentialEntity, bool> guard, Action<MfaCredentialEntity> apply,
        CancellationToken ct, bool touchLastUsed = true)
    {
        MfaCredentialEntity entity;
        try
        {
            var response = await credentialsTable.GetEntityAsync<MfaCredentialEntity>(
                partitioner.PK(userId), credentialId, cancellationToken: ct);
            entity = response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }

        if (!guard(entity)) return false;

        apply(entity);
        // Skipped for the legacy-hash upgrade: it sweeps the user's whole recovery set, so stamping
        // would mark every code as used because one of them was.
        if (touchLastUsed) entity.LastUsedAt = DateTimeOffset.UtcNow;

        try
        {
            await credentialsTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
        {
            return false;
        }
    }

    public async Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        // Clear the WebAuthn credential-id index row too — a deleted authenticator must leave no
        // stale lookup pointing at a gone credential (and must be re-registrable).
        var existing = await GetCredentialAsync(userId, credentialId, ct);
        if (existing is not null)
            await DeleteWebAuthnIndexForAsync(existing, ct);

        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("MfaCredentials", pk, credentialId, ct);
        try
        {
            await credentialsTable.DeleteEntityAsync(pk, credentialId, cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    public async Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        // Tombstone-first (F24e): materialize the keys, record the whole batch, then delete — a crash
        // mid-way can no longer leave a delete that no backup ever sees.
        var pk = partitioner.PK(userId);
        var keys = new List<(string, string)>();
        await foreach (var entity in credentialsTable.QueryAsync<MfaCredentialEntity>(
            e => e.PartitionKey == pk, cancellationToken: ct))
        {
            keys.Add((entity.PartitionKey, entity.RowKey));
            // Remove the matching WebAuthn index row so no stale lookup survives the reset.
            await DeleteWebAuthnIndexForAsync(entity.ToModel(), ct);
        }

        if (tombstoneWriter is not null && keys.Count > 0)
            await tombstoneWriter.WriteBatchAsync("MfaCredentials", keys, ct);

        foreach (var (kpk, krk) in keys)
        {
            try
            {
                await credentialsTable.DeleteEntityAsync(kpk, krk, cancellationToken: ct);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted
            }
        }
    }

    public async Task<(string UserId, string CredentialId)?> FindByWebAuthnCredentialIdAsync(
        byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        try
        {
            var response = await webAuthnIndexTable.GetEntityAsync<MfaWebAuthnIndexEntity>(
                partitioner.PK(hash), MfaWebAuthnIndexEntity.LookupRowKey, cancellationToken: ct);
            return (response.Value.UserId, response.Value.CredentialId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> TryStoreWebAuthnCredentialIdMappingAsync(
        byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        var entity = new MfaWebAuthnIndexEntity
        {
            PartitionKey = partitioner.PK(hash),
            RowKey = MfaWebAuthnIndexEntity.LookupRowKey,
            UserId = userId,
            CredentialId = credentialId,
        };
        try
        {
            // Add, not Upsert — 409 means someone already owns this credential id, and the loser of the
            // race must be told so rather than quietly repointing the index at itself.
            await webAuthnIndexTable.AddEntityAsync(entity, ct);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    // Delete the WebAuthn credential-id index row for a credential (no-op for TOTP/recovery or missing
    // data). No try/catch: TryGetWebAuthnCredentialId absorbs malformed JSON/base64 (returns false), and
    // the storage delete is left to PROPAGATE a transient fault rather than silently leave the stale row.
    private async Task DeleteWebAuthnIndexForAsync(MfaCredential credential, CancellationToken ct)
    {
        if (credential.TryGetWebAuthnCredentialId(out var credentialId))
            await DeleteWebAuthnCredentialIdMappingAsync(credentialId, ct);
    }

    public async Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(HashWebAuthnCredentialId(webAuthnCredentialId));
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("MfaWebAuthnIndex", pk, MfaWebAuthnIndexEntity.LookupRowKey, ct);
        try
        {
            await webAuthnIndexTable.DeleteEntityAsync(pk, MfaWebAuthnIndexEntity.LookupRowKey, cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    public async Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default)
    {
        var entity = MfaChallengeEntity.FromModel(challenge);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await challengesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        try
        {
            var response = await challengesTable.GetEntityAsync<MfaChallengeEntity>(
                partitioner.PK(challengeId), MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

            var entity = response.Value;
            if (entity.IsConsumed || entity.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            return entity.ToModel();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<MfaChallenge?> ConsumeChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(challengeId);
        try
        {
            var response = await challengesTable.GetEntityAsync<MfaChallengeEntity>(
                pk, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

            var entity = response.Value;

            // Delete immediately to prevent replay (same pattern as OidcStateStore)
            await challengesTable.DeleteEntityAsync(pk, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

            if (entity.IsConsumed || entity.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            return entity.ToModel();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string HashWebAuthnCredentialId(byte[] credentialId)
    {
        var hash = SHA256.HashData(credentialId);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
