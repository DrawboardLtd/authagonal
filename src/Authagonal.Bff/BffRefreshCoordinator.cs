using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Bff;

/// <summary>
/// Keeps a session's access token fresh, collapsing concurrent refreshes for one session into a
/// single redemption of the rotating refresh token — WITHIN ONE PROCESS.
/// </summary>
/// <remarks>
/// The previous summary claimed single-flight outright. The mechanism is an in-process dictionary of
/// semaphores, so it holds no mutual exclusion across replicas — and AddAuthagonalBff explicitly
/// supports multi-instance deployment (that is why it asks for a shared IDistributedCache, which is
/// where the session and its refresh token live). Two replicas could therefore read the same session,
/// both see it needs refreshing, and both redeem the same token.
/// <para>
/// That is indistinguishable from a stolen-token replay, and the IdP's response to replay is to
/// revoke the whole grant family — so the documented multi-instance deployment could sign a user out
/// everywhere as a matter of routine. The absorbing mechanism is the IdP's refresh-reuse grace
/// window, which returns the successor idempotently instead of revoking; it exists in the protocol
/// layer (30s) but the Server host was overriding it to 0, disabling it. That default is now aligned.
/// </para>
/// </remarks>
internal sealed class BffRefreshCoordinator(
    ITokenClient tokens,
    IBffSessionStore store,
    IBffTenantResolver tenants,
    IOptions<AuthagonalBffOptions> options,
    ILogger<BffRefreshCoordinator> logger)
{
    private readonly AuthagonalBffOptions _o = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>Returns the session with a valid access token, refreshing if it's near expiry. Returns null
    /// when the session can no longer be kept valid (refresh failed / already expired with no refresh
    /// token); callers should treat null as "logged out".</summary>
    public async Task<BffSession?> EnsureFreshAsync(BffSession session, CancellationToken ct = default)
    {
        if (!NeedsRefresh(session))
            return session;

        if (session.RefreshToken is null)
            return session.AccessTokenExpiresAt > DateTimeOffset.UtcNow ? session : null;

        var gate = _gates.GetOrAdd(session.SessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read: a concurrent request holding the gate first may have already refreshed.
            var current = await store.GetAsync(session.SessionId, ct);
            if (current is null)
                return null; // logged out underneath us
            if (!NeedsRefresh(current))
                return current;
            if (current.RefreshToken is null)
                return current.AccessTokenExpiresAt > DateTimeOffset.UtcNow ? current : null;

            var tenant = await tenants.ResolveAsync(current.TenantKey, ct);
            if (tenant is null)
            {
                // The tenant is no longer resolvable (deprovisioned / config changed); the session can't be
                // kept valid without its client credentials — treat as logged out.
                logger.LogWarning("BFF refresh: tenant '{TenantKey}' no longer resolvable for session {SessionId}.", current.TenantKey, current.SessionId);
                await store.RemoveAsync(current.SessionId, ct);
                return null;
            }

            try
            {
                var result = await tokens.RefreshAsync(tenant, current.RefreshToken, ct);
                current.AccessToken = result.AccessToken;
                current.RefreshToken = result.RefreshToken ?? current.RefreshToken;
                if (result.IdToken is not null)
                    current.IdToken = result.IdToken;
                current.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);
                await store.SetAsync(current, ct);
                return current;
            }
            catch (BffTokenException ex)
            {
                logger.LogWarning(ex, "BFF refresh failed for session {SessionId}; treating as logged out.", current.SessionId);
                await store.RemoveAsync(current.SessionId, ct);
                return null;
            }
        }
        finally
        {
            gate.Release();
            // Dropped once nobody is waiting on it. Retaining one semaphore per session id for the
            // process lifetime is an unbounded leak on a long-lived BFF with churning sessions.
            if (gate.CurrentCount == 1 && _gates.TryRemove(session.SessionId, out var removed)
                && !ReferenceEquals(removed, gate))
            {
                // Someone swapped in a different gate between the check and the removal; put it back
                // rather than orphaning a lock another request is using.
                _gates.TryAdd(session.SessionId, removed);
            }
        }
    }

    private bool NeedsRefresh(BffSession session)
        => session.AccessTokenExpiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(_o.RefreshThresholdSeconds);
}
