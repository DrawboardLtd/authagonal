using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IRevokedTokenStore"/>. All revocations share one partition ("revoked"), sk = jti.
/// An entry counts only until the token's natural expiry (checked on read); the row also carries that
/// expiry as its TTL so the reaper clears it afterwards rather than letting the list grow forever.
/// </summary>
public sealed class SqlRevokedTokenStore(SqlTable table, EnvPartitioner partitioner) : IRevokedTokenStore
{
    private const string Partition = "revoked";

    public Task AddAsync(string jti, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return Task.CompletedTask;

        var row = new SqlRow(partitioner.PK(Partition), jti) { ExpiresAt = expiresAt };
        row.PutDate("expiresAt", expiresAt);
        row.PutS("clientId", clientId);
        row.PutDate("revokedAt", DateTimeOffset.UtcNow);
        return table.PutAsync(row, ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimOnceAsync(
        string key, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var row = new SqlRow(partitioner.PK(Partition), key) { ExpiresAt = expiresAt };
        row.PutDate("expiresAt", expiresAt);
        row.PutS("clientId", clientId);
        row.PutDate("revokedAt", DateTimeOffset.UtcNow);

        if (await table.PutIfAbsentAsync(row, ct).ConfigureAwait(false)) return true;

        // Present. An entry past its own expiry protects nothing — IsRevokedAsync says as much — so
        // the key is reclaimable; anything still live is a genuine replay.
        var existing = await table.GetAsync(partitioner.PK(Partition), key, ct: ct).ConfigureAwait(false);
        if (existing is not null && existing.GetDate("expiresAt") > DateTimeOffset.UtcNow) return false;

        await table.PutAsync(row, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;
        var row = await table.GetAsync(partitioner.PK(Partition), jti, ct: ct).ConfigureAwait(false);
        // Past the token's natural expiry it's invalid for lifetime reasons anyway — treat as not revoked.
        return row is not null && row.GetDate("expiresAt") > DateTimeOffset.UtcNow;
    }
}
