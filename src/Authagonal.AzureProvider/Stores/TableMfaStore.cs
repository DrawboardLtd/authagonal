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
            results.Add(ToModel(entity));
        }
        return results;
    }

    public async Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        try
        {
            var response = await credentialsTable.GetEntityAsync<MfaCredentialEntity>(
                partitioner.PK(userId), credentialId, cancellationToken: ct);
            return ToModel(response.Value);
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
    public Task<bool> TryRecordWebAuthnUseAsync(
        string userId, string credentialId, uint signCount, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, e => e.SignCount <= signCount, e => e.SignCount = signCount, ct);

    /// <inheritdoc />
    public Task<bool> TryActivateCredentialAsync(
        string userId, string credentialId, string name, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, _ => true, e => e.Name = name, ct);

    /// <inheritdoc />
    public Task<bool> TryUpgradeRecoverySecretAsync(
        string userId, string credentialId, string secretProtected, CancellationToken ct = default)
        => ClaimAsync(userId, credentialId, e => !e.IsConsumed,
            e => e.SecretProtected = secretProtected, ct, touchLastUsed: false);

    /// <summary>
    /// Entity to model, with the environment prefix taken back off the key that carries it.
    /// </summary>
    /// <remarks>
    /// <c>MfaCredentialEntity.ToModel</c> sets <c>UserId</c> from <c>PartitionKey</c>, and the stored
    /// partition key is <c>partitioner.PK(userId)</c> — so on any non-live environment the model came back
    /// carrying <c>env|userId</c> as its user id. Every write path then re-applies the prefix
    /// (<c>FromModel</c> puts <c>UserId</c> in <c>PartitionKey</c> and the store PKs it again), so the row
    /// landed at <c>env|env|userId</c>: a phantom the reader never looks at. Invisible on the live
    /// environment, where the prefix is empty and <c>PK(x) == x</c>, which is exactly why it survived.
    /// <para>
    /// Same shape as <c>TableUserStore</c>, which strips in the store for the same reason. The entity types
    /// have no partitioner, and giving them one would put the environment's identity in a DTO.
    /// </para>
    /// </remarks>
    private MfaCredential ToModel(MfaCredentialEntity entity)
    {
        var model = entity.ToModel();
        model.UserId = partitioner.Strip(model.UserId);
        return model;
    }

    /// <summary>
    /// As <see cref="ToModel(MfaCredentialEntity)"/>, for a challenge — where the consequence was worse
    /// than a phantom row.
    /// </summary>
    /// <remarks>
    /// <c>ChallengeId</c> comes from <c>PartitionKey</c> here, and both callers of a fetched challenge feed
    /// that id straight back in: <c>StoreChallengeAsync</c> for the attempt counter and
    /// <c>ConsumeChallengeAsync</c> for anti-replay. Doubly prefixed, the increment landed in a row nothing
    /// reads — so the five-attempt cap on MFA verification never bound — and the consume deleted that
    /// phantom while leaving the real challenge intact, so a verified challenge stayed replayable for its
    /// whole lifetime. Both on any non-live environment, on the Azure provider.
    /// </remarks>
    private MfaChallenge ToModel(MfaChallengeEntity entity)
    {
        var model = entity.ToModel();
        model.ChallengeId = partitioner.Strip(model.ChallengeId);
        return model;
    }

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
            await DeleteWebAuthnIndexForAsync(ToModel(entity), ct);
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

            return ToModel(entity);
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

            if (entity.IsConsumed || entity.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            // The winner is decided by a compare-and-set on the EXISTING row, not by the delete.
            //
            // This used to read the entity and then issue an unconditional delete, and the Azure SDK documents
            // that overload as "should not fail because the entity does not exist" — so every concurrent caller
            // that got past its own read also got a successful delete and the challenge back. Measured against
            // Azurite: 8 concurrent consumes of one challenge returned SIX non-null. A single-use MFA challenge
            // six callers can spend is not single-use, and it is the anti-replay guard on the second factor.
            //
            // A CONDITIONAL delete is not sufficient either, and this is the part worth remembering: Azurite
            // answers a conditional delete of an already-deleted row with success rather than 404, so the
            // delete's status cannot be the arbiter. (A stale-ETag delete against a row that still exists IS
            // refused with 412 — verified — so the precondition works; it is the missing-row case that does
            // not.) Marking consumed via UpdateEntityAsync fails unambiguously with 412 for every loser,
            // because the row is still there to compare against. Same shape as
            // TableGrantStore.TryMarkConsumedAsync.
            entity.IsConsumed = true;
            try
            {
                await challengesTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                // 412: another caller claimed it first. 404: already gone. Either way, not ours to spend.
                return null;
            }

            // Claimed. The row is now redundant — delete it so it cannot be re-read, best effort: the claim
            // above is what makes this single-use, so a failed delete leaves a consumed row that
            // GetChallengeAsync and this method both already refuse.
            try
            {
                await challengesTable.DeleteEntityAsync(pk, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);
            }
            catch (Azure.RequestFailedException) { /* consumed already; expiry sweeps the row */ }

            return ToModel(entity);
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
