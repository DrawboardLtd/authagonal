using System.Collections.Concurrent;
using Authagonal.Core.Clustering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Bff;

/// <summary>
/// Keeps a session's access token fresh, collapsing concurrent refreshes for one session into a
/// single redemption of the rotating refresh token.
/// </summary>
/// <remarks>
/// The in-process mechanism is a dictionary of semaphores, which holds no mutual exclusion across
/// replicas — and AddAuthagonalBff explicitly supports multi-instance deployment (that is why it asks
/// for a shared IDistributedCache, which is where the session and its refresh token live). Two
/// replicas could therefore read the same session, both see it needs refreshing, and both redeem the
/// same token. That is indistinguishable from a stolen-token replay, and the IdP's response to replay
/// is to revoke the whole grant family — so the documented multi-instance deployment could sign a user
/// out everywhere as a matter of routine.
/// <para>
/// So the gate extends across replicas when the host registers an <see cref="ILeaseProvider"/>: the
/// redemption happens under a lease named for the session, and a replica that cannot take the lease
/// waits for the holder's result and uses the session IT stored instead of redeeming anything. Any
/// backend works — the Azure/AWS/SQL providers each ship one (AddAuthagonalClustering) — because all
/// this needs is "at most one holder", which is what the leader-election lease already guarantees.
/// </para>
/// <para>
/// With NO lease provider registered the single-flight is process-local, exactly as before. A
/// multi-instance BFF in that configuration still needs the IdP's refresh-reuse grace window
/// (<c>Auth:RefreshTokenReuseGraceSeconds</c>, 30 in the protocol layer, 0 — strict — in the Server
/// host's own default) to absorb the double redemption, or it will sign users out under ordinary
/// concurrent load.
/// </para>
/// </remarks>
internal sealed class BffRefreshCoordinator(
    ITokenClient tokens,
    IBffSessionStore store,
    IBffTenantResolver tenants,
    IOptions<AuthagonalBffOptions> options,
    ILogger<BffRefreshCoordinator> logger,
    ILeaseProvider? leases = null)
{
    private readonly AuthagonalBffOptions _o = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Identifies this coordinator to the lease provider, so a renewal is distinguishable from a
    /// takeover. Per INSTANCE, not per process: the coordinator is a singleton, so that is one id per
    /// replica in production, and it keeps two coordinators constructed side by side (a test standing
    /// in for two replicas) from being mistaken for one holder renewing its own lease.
    /// </summary>
    private readonly string _nodeId = $"bff-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>
    /// How long a refresh may hold the session's lease. Long enough that a token round-trip fits inside
    /// it, short enough that a replica killed mid-refresh does not park the session for long — the
    /// lease expiring IS the recovery path. (Azure blob leases clamp to 15-60s, so 15 is the floor that
    /// survives every shipped backend unchanged.)
    /// </summary>
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for the holder's result before giving up on the lease.</summary>
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LeasePollInterval = TimeSpan.FromMilliseconds(100);

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
        var leaseKey = $"bff-refresh:{session.TenantKey}:{session.SessionId}";
        var holdsLease = false;
        try
        {
            if (leases is not null)
            {
                var (acquired, peerResult) = await AcquireOrFollowAsync(session, leaseKey, ct);
                if (!acquired)
                    return peerResult;
                holdsLease = true;
            }

            // Re-read: a concurrent request holding the gate (or, across replicas, the lease) may have
            // already refreshed.
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
            // Released before the semaphore, and with CancellationToken.None: a cancelled request that
            // kept the lease would park every other replica on this session until the TTL ran out.
            if (holdsLease)
                await leases!.ReleaseAsync(leaseKey, _nodeId, CancellationToken.None);

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

    /// <summary>
    /// Takes the session's refresh lease, or — if another replica holds it — waits for that replica's
    /// result and returns the session IT stored. Returns (true, null) when this node may redeem.
    /// </summary>
    /// <remarks>
    /// The waiting side must never fall through to its own redemption: redeeming the same rotating
    /// token the holder is redeeming is precisely the "replay" the IdP answers by revoking the whole
    /// grant family. So when the wait runs out (a holder that is alive but stuck — a dead one's lease
    /// expires and we acquire) the answer is the session as it stands, not a second redemption. The
    /// token is normally still valid at that point, because the refresh threshold fires well before
    /// expiry; if it is not, the session is treated as logged out.
    /// </remarks>
    private async Task<(bool Acquired, BffSession? PeerResult)> AcquireOrFollowAsync(
        BffSession session, string leaseKey, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + LeaseWait;

        while (!await leases!.TryAcquireOrRenewAsync(leaseKey, _nodeId, LeaseTtl, ct))
        {
            var peer = await store.GetAsync(session.SessionId, ct);
            if (peer is null)
                return (false, null); // logged out underneath us
            if (!NeedsRefresh(peer))
                return (false, peer); // the holder refreshed it — use its tokens, don't mint more

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "BFF refresh: another instance has held the refresh lease for session {SessionId} " +
                    "longer than {Seconds}s. Serving the session unrefreshed rather than redeeming the " +
                    "same refresh token, which the IdP would read as replay.",
                    session.SessionId, LeaseWait.TotalSeconds);
                return (false, peer.AccessTokenExpiresAt > DateTimeOffset.UtcNow ? peer : null);
            }

            await Task.Delay(LeasePollInterval, ct);
        }

        return (true, null);
    }

    private bool NeedsRefresh(BffSession session)
        => session.AccessTokenExpiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(_o.RefreshThresholdSeconds);
}
