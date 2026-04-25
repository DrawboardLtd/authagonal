using System.Security.Cryptography;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableMfaStore(
    TableClient credentialsTable,
    TableClient challengesTable,
    TableClient webAuthnIndexTable,
    EnvPartitioner partitioner,
    ITombstoneWriter? tombstoneWriter = null) : IMfaStore
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
        try
        {
            await credentialsTable.DeleteEntityAsync(pk, credentialId, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("MfaCredentials", pk, credentialId, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    public async Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(userId);
        var tombstones = new List<(string, string)>();
        await foreach (var entity in credentialsTable.QueryAsync<MfaCredentialEntity>(
            e => e.PartitionKey == pk, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
        {
            try
            {
                await credentialsTable.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                tombstones.Add((entity.PartitionKey, entity.RowKey));
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted
            }
        }

        if (tombstoneWriter is not null && tombstones.Count > 0)
            await tombstoneWriter.WriteBatchAsync("MfaCredentials", tombstones, ct);
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

    public async Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(HashWebAuthnCredentialId(webAuthnCredentialId));
        try
        {
            await webAuthnIndexTable.DeleteEntityAsync(pk, MfaWebAuthnIndexEntity.LookupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("MfaWebAuthnIndex", pk, MfaWebAuthnIndexEntity.LookupRowKey, ct);
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
