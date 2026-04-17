namespace Authagonal.Core.Stores;

/// <summary>
/// Tracks revoked access token identifiers (jti) until their natural expiry. Entries are
/// automatically skipped / cleaned up once the corresponding token has expired.
/// </summary>
public interface IRevokedTokenStore
{
    Task AddAsync(string jti, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default);
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);
}
