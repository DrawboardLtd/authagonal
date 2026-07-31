namespace Authagonal.Core.Stores;

/// <summary>
/// Tracks revoked access token identifiers (jti) until their natural expiry. Entries are
/// automatically skipped / cleaned up once the corresponding token has expired.
/// </summary>
public interface IRevokedTokenStore
{
    Task AddAsync(string jti, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default);
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);

    /// <summary>
    /// Claims <paramref name="key"/> for the first caller only. True means this call recorded it;
    /// false means it was already present.
    /// </summary>
    /// <remarks>
    /// This store doubles as the single-use ledger for RFC 7523 client-assertion jtis, and that was
    /// implemented as IsRevokedAsync followed by AddAsync — a check-then-act with no atomicity, over
    /// backends whose AddAsync is an unconditional upsert. Two requests carrying the same assertion
    /// both read "not seen", both write, and both authenticate, which is precisely the single-use
    /// property the jti exists to provide. The codebase uses conditional writes for exactly this
    /// everywhere else (the SAML assertion caches on all three backends), so this is the same
    /// primitive, exposed where replay protection actually needs it.
    ///
    /// The default implementation is the old non-atomic pair, so an external IRevokedTokenStore keeps
    /// compiling and keeps its current behaviour; every backend in this repository overrides it.
    /// </remarks>
    async Task<bool> TryClaimOnceAsync(
        string key, DateTimeOffset expiresAt, string? clientId = null, CancellationToken ct = default)
    {
        if (await IsRevokedAsync(key, ct).ConfigureAwait(false)) return false;
        await AddAsync(key, expiresAt, clientId, ct).ConfigureAwait(false);
        return true;
    }
}
