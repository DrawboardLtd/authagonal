using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableRevokedTokenStore(TableClient revokedTokensTable, EnvPartitioner partitioner) : IRevokedTokenStore
{
    public async Task AddAsync(string jti, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return;

        var entity = new RevokedTokenEntity
        {
            PartitionKey = partitioner.PK(RevokedTokenEntity.RevokedPartition),
            RowKey = jti,
            ExpiresAt = expiresAt,
            ClientId = clientId,
            RevokedAt = DateTimeOffset.UtcNow,
        };

        await revokedTokensTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimOnceAsync(
        string key, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var entity = new RevokedTokenEntity
        {
            PartitionKey = partitioner.PK(RevokedTokenEntity.RevokedPartition),
            RowKey = key,
            ExpiresAt = expiresAt,
            ClientId = clientId,
            RevokedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await revokedTokensTable.AddEntityAsync(entity, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Already claimed. An entry past its own expiry no longer protects anything, so the key
            // is reclaimable — same rule IsRevokedAsync applies on read.
            try
            {
                var existing = await revokedTokensTable.GetEntityAsync<RevokedTokenEntity>(
                    entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                if (existing.Value.ExpiresAt > DateTimeOffset.UtcNow) return false;

                await revokedTokensTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
                return true;
            }
            catch (RequestFailedException inner) when (inner.Status == 404)
            {
                // Deleted between the conflict and the read; the claim is free again.
                await revokedTokensTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
                return true;
            }
        }
    }

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;

        try
        {
            var response = await revokedTokensTable.GetEntityAsync<RevokedTokenEntity>(
                partitioner.PK(RevokedTokenEntity.RevokedPartition), jti, cancellationToken: ct);
            // Entries remain until the token would have expired anyway; if we're past that,
            // the token is already invalid for lifetime reasons and we can ignore the entry.
            return response.Value.ExpiresAt > DateTimeOffset.UtcNow;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
