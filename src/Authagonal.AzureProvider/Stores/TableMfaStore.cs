using System.Security.Cryptography;
using System.Text.Json;
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

    public async Task StoreWebAuthnCredentialIdMappingAsync(
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
        await webAuthnIndexTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    // Deletes the WebAuthn credential-id index row for a credential, if it is a WebAuthn factor
    // whose PublicKeyJson carries a credential id. No-op for TOTP/recovery. Best-effort: a parse
    // failure or missing row must not block the credential delete.
    private async Task DeleteWebAuthnIndexForAsync(MfaCredential credential, CancellationToken ct)
    {
        if (credential.Type != MfaCredentialType.WebAuthn || string.IsNullOrEmpty(credential.PublicKeyJson))
            return;

        byte[] credentialId;
        try
        {
            using var doc = JsonDocument.Parse(credential.PublicKeyJson);
            string? credIdB64 = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "credentialId", StringComparison.OrdinalIgnoreCase))
                {
                    credIdB64 = prop.Value.GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(credIdB64))
                return;
            credentialId = Convert.FromBase64String(credIdB64);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Malformed JSON / bad base64 — nothing to clean, and never block the delete.
            return;
        }

        // The storage delete is OUTSIDE the try on purpose: a transient storage failure (or
        // cancellation) MUST propagate, not be swallowed — swallowing it leaves the exact stale index
        // row this method exists to remove.
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
