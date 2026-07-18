namespace Authagonal.Core.Services;

/// <summary>A server-side SSO session belonging to a user, for self-service listing + revocation.</summary>
public sealed record SessionDescriptor(
    string SessionId,
    bool Current,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ExpiresAt,
    string Ip,
    string UserAgent);

/// <summary>
/// Optional companion to a server-side <c>ITicketStore</c>: lets a user enumerate and revoke their own
/// active sessions. Register an implementation to light up the account page's "active sessions" section and
/// the <c>/api/account/sessions</c> endpoints; without one, session self-management is simply absent.
/// </summary>
public interface IUserSessionRegistry
{
    /// <summary>HttpContext.Items key an <c>ITicketStore</c> should stash the current request's session id
    /// under (during retrieval), so "this session" can be flagged and protected. Shared convention.</summary>
    public const string CurrentSessionItem = "authagonal.session_id";

    /// <summary>A user's active sessions, newest activity first. <paramref name="currentSessionId"/> (if
    /// known) is flagged as the caller's own.</summary>
    Task<IReadOnlyList<SessionDescriptor>> ListAsync(string userId, string? currentSessionId, CancellationToken ct = default);

    /// <summary>Revoke one of the user's sessions. Returns false if it wasn't found for that user.</summary>
    Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>Revoke all the user's sessions except <paramref name="keepSessionId"/> (typically the
    /// caller's current one). Returns the count revoked.</summary>
    Task<int> RevokeOthersAsync(string userId, string? keepSessionId, CancellationToken ct = default);
}
