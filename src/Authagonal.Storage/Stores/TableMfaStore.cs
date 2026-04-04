using System.Security.Cryptography;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableMfaStore(
    TableClient credentialsTable,
    TableClient challengesTable,
    TableClient webAuthnIndexTable) : IMfaStore
{
    public async Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var results = new List<MfaCredential>();
        await foreach (var entity in credentialsTable.QueryAsync<MfaCredentialEntity>(
            e => e.PartitionKey == userId, cancellationToken: ct))
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
                userId, credentialId, cancellationToken: ct);
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
        await credentialsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default)
    {
        var entity = MfaCredentialEntity.FromModel(credential);
        await credentialsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        try
        {
            await credentialsTable.DeleteEntityAsync(userId, credentialId, cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    public async Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        await foreach (var entity in credentialsTable.QueryAsync<MfaCredentialEntity>(
            e => e.PartitionKey == userId, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
        {
            try
            {
                await credentialsTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
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
                hash, MfaWebAuthnIndexEntity.LookupRowKey, cancellationToken: ct);
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
            PartitionKey = hash,
            RowKey = MfaWebAuthnIndexEntity.LookupRowKey,
            UserId = userId,
            CredentialId = credentialId,
        };
        await webAuthnIndexTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        try
        {
            await webAuthnIndexTable.DeleteEntityAsync(hash, MfaWebAuthnIndexEntity.LookupRowKey, cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    public async Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default)
    {
        var entity = MfaChallengeEntity.FromModel(challenge);
        await challengesTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        try
        {
            var response = await challengesTable.GetEntityAsync<MfaChallengeEntity>(
                challengeId, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

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
        try
        {
            var response = await challengesTable.GetEntityAsync<MfaChallengeEntity>(
                challengeId, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

            var entity = response.Value;

            // Delete immediately to prevent replay (same pattern as OidcStateStore)
            await challengesTable.DeleteEntityAsync(challengeId, MfaChallengeEntity.ChallengeRowKey, cancellationToken: ct);

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
