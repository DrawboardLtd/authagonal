using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

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
