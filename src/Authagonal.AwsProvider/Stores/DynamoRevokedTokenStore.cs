using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IRevokedTokenStore"/>. All revocations share one partition ("revoked"),
/// sk = jti. An entry counts only until the token's natural expiry (checked on read).</summary>
public sealed class DynamoRevokedTokenStore(DynamoTable table, EnvPartitioner partitioner) : IRevokedTokenStore
{
    private const string Partition = "revoked";

    public Task AddAsync(string jti, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return Task.CompletedTask;
        var item = Dyn.Item(partitioner.PK(Partition), jti);
        item.PutDate("expiresAt", expiresAt);
        item.PutS("clientId", clientId);
        item.PutDate("revokedAt", DateTimeOffset.UtcNow);
        // A revocation stops mattering at the token's own expiry — IsRevokedAsync already says so —
        // so the row has nothing left to protect after it. A grace margin keeps the entry alive
        // across clock skew and DynamoDB's best-effort deletion window.
        item.PutTtl(expiresAt.AddDays(1));
        return table.PutAsync(item, ct);
    }

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;
        var item = await table.GetAsync(partitioner.PK(Partition), jti, ct).ConfigureAwait(false);
        // Past the token's natural expiry it's invalid for lifetime reasons anyway — treat as not revoked.
        return item is not null && item.GetDate("expiresAt") > DateTimeOffset.UtcNow;
    }
}
