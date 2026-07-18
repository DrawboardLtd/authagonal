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
    Task<int> RemoveBySidAsync(string sid, CancellationToken ct = default);

    /// <summary>Delete every session for a subject (a subject-scoped back-channel logout — the form
    /// Authagonal emits). Returns the count removed.</summary>
    Task<int> RemoveBySubjectAsync(string subject, CancellationToken ct = default);
}
