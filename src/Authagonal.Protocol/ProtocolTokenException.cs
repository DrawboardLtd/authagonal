namespace Authagonal.Protocol;

/// <summary>
/// A token-request failure that maps to a specific OAuth error code — richer than the
/// message-prefix sniffing the legacy <see cref="InvalidOperationException"/> paths use.
/// Thrown by the agentic delegation paths; the token endpoints translate it verbatim.
/// </summary>
public class ProtocolTokenException(string error, string description) : Exception(description)
{
    public string Error { get; } = error;
    public string Description { get; } = description;
}

/// <summary>
/// The delegated exchange is parked on a pending approval (RFC 8628-style semantics):
/// the client receives <c>authorization_pending</c> plus the <c>approval_id</c> to poll with.
/// </summary>
public sealed class ApprovalPendingException(string approvalId, int intervalSeconds)
    : ProtocolTokenException("authorization_pending", "The user has not yet approved the request")
{
    public string ApprovalId { get; } = approvalId;
    public int IntervalSeconds { get; } = intervalSeconds;
}
