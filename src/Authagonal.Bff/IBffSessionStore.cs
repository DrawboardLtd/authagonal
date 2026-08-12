namespace Authagonal.Bff;

/// <summary>Server-side storage for BFF sessions. The default implementation is backed by
/// <c>IDistributedCache</c>; replace it to move sessions onto other infrastructure (this is one of the
/// three seams that let the BFF core run hosted at the edge later).</summary>
public interface IBffSessionStore
{
    /// <summary>Load a session by its opaque id, or null if absent/expired.</summary>
    Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Create or replace a session. Implementations must also index it by
    /// <see cref="BffSession.Subject"/> and (when present) <see cref="BffSession.Sid"/> so the
    /// back-channel-logout removals below can find it.</summary>
    Task SetAsync(BffSession session, CancellationToken ct = default);

    /// <summary>Delete a session by id.</summary>
    Task RemoveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Delete every session matching an OIDC <c>sid</c> (a session-scoped back-channel logout).
    /// Returns the count removed.</summary>
    /// <param name="tenantKey">
    /// Scopes the lookup to one tenant. `sub` is unique only within an issuer, so an unscoped removal
    /// let a logout from one tenant's IdP terminate another tenant's sessions for a colliding subject.
    /// </param>
    Task<int> RemoveBySidAsync(string sid, string? tenantKey = null, CancellationToken ct = default);

    /// <summary>Delete every session for a subject (a subject-scoped back-channel logout — the form
    /// Authagonal emits). Returns the count removed.</summary>
    /// <inheritdoc cref="RemoveBySidAsync"/>
    Task<int> RemoveBySubjectAsync(string subject, string? tenantKey = null, CancellationToken ct = default);
}

/// <summary>
/// OPTIONAL cross-replica lock over one session's refresh, implemented by an
/// <see cref="IBffSessionStore"/> whose backend has a conditional-write primitive to build one on.
/// </summary>
/// <remarks>
/// Without a cross-replica gate <see cref="BffRefreshCoordinator"/>'s single-flight is per-PROCESS — a
/// dictionary of semaphores on one instance — while the session and its rotating refresh token live in a
/// store every replica shares. Two replicas can therefore read the same session, both find it needs
/// refreshing, and both redeem the same refresh token. That is indistinguishable from a stolen-token
/// replay, and an IdP's answer to replay is to revoke the whole grant family — so the multi-instance
/// deployment the README recommends can sign a user out everywhere under nothing more than concurrent
/// load.
/// <para>
/// There are two ways to supply that gate and the coordinator accepts either: register an
/// <see cref="Core.Clustering.ILeaseProvider"/> in the container, or implement this interface on the
/// session store. The lease provider came first and stays the primary route because a host that already
/// runs Authagonal's clustering has one; this interface exists because the DEFAULT store is backed by
/// <c>IDistributedCache</c>, which offers no conditional write — there is no set-if-absent on it — so the
/// library cannot build the lock out of the primitive it already requires. A host on Redis can, in about
/// as many lines as <c>SET NX PX</c> takes to write, and this is where that goes. It is the .NET twin of
/// <c>acquireRefreshLock</c> / <c>releaseRefreshLock</c> on the TypeScript package's own session store.
/// </para>
/// <para>
/// A separate interface rather than optional members on <see cref="IBffSessionStore"/>, because the
/// coordinator has to be able to TELL: a default interface method cannot be distinguished from an
/// override at runtime, and the difference here is between a real lock and a silent no-lock. Implement it
/// on the same class as the store — <c>class MyStore : IBffSessionStore, IBffRefreshLockStore</c> — and a
/// type test finds it.
/// </para>
/// <para>
/// With neither route supplied the behaviour is unchanged and process-local, and a multi-instance BFF
/// then rests entirely on the IdP's refresh-reuse grace window
/// (<c>Auth:RefreshTokenReuseGraceSeconds</c>, 30 in the protocol layer but <b>0 — strict</b> in the
/// Authagonal.Server host's own default) to absorb the double redemption.
/// <see cref="BffRefreshGateWarning"/> says so at startup rather than leaving it to be discovered from a
/// support ticket.
/// </para>
/// </remarks>
public interface IBffRefreshLockStore
{
    /// <summary>
    /// Takes the refresh lock for <paramref name="sessionId"/>, or returns false if another holder has it.
    /// </summary>
    /// <remarks>
    /// All this needs is "at most one holder for a short time" — the same guarantee
    /// <see cref="Core.Clustering.ILeaseProvider"/> gives, without the renewal or the node identity, since
    /// a refresh either finishes inside <paramref name="ttl"/> or is better abandoned. Implement it as a
    /// conditional write that expires on its own: the lock lapsing IS the recovery path for a replica
    /// killed mid-refresh, so it must never be taken without a TTL.
    /// </remarks>
    Task<bool> TryAcquireRefreshLockAsync(string sessionId, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Releases <see cref="TryAcquireRefreshLockAsync"/>. Best-effort; never throws.</summary>
    Task ReleaseRefreshLockAsync(string sessionId, CancellationToken ct = default);
}
