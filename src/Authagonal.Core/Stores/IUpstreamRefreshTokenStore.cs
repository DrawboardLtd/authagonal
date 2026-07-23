namespace Authagonal.Core.Stores;

/// <summary>
/// Durable store for the single upstream (federated IdP) refresh token backing one federated browser
/// session, keyed by (userId, connectionId, sessionId).
/// </summary>
/// <remarks>
/// A federated login yields ONE upstream refresh token, shared by every local RP grant derived from that
/// session. It rotates in place as the session refreshes, so a later <c>/connect/authorize</c> (a second
/// RP, a silent re-auth) reads the CURRENT token rather than a login-time copy pinned on the cookie —
/// which, once the upstream rotated it (Entra/Auth0 one-time-use), would be dead and kill the new grant on
/// its first refresh. The <c>sessionId</c> (<c>sid</c>) is part of the key on purpose: a user's second
/// device has its own federated login and its own upstream token, so they must not clobber each other.
/// The token is a bearer credential — implementations encrypt it at rest with the same field cipher the
/// grant store uses.
/// </remarks>
public interface IUpstreamRefreshTokenStore
{
    /// <summary>Store (or replace) the upstream refresh token for a federated session. <paramref name="expiresAt"/>
    /// bounds how long an abandoned row lives.</summary>
    Task SetAsync(string userId, string connectionId, string sessionId, string refreshToken, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>Read the current upstream refresh token for a federated session, or null if none or expired.</summary>
    Task<string?> GetAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default);

    /// <summary>Remove the stored token (e.g. on upstream revocation or logout).</summary>
    Task RemoveAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default);
}
