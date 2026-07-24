namespace Authagonal.Core.Authority;

/// <summary>Claim and request-parameter names for the agentic authority model.</summary>
public static class AuthorityClaims
{
    /// <summary>RFC 9396 §9.1 — the fine-grained authority claim on an issued token, and the
    /// request parameter that narrows it at the token endpoint.</summary>
    public const string AuthorizationDetails = "authorization_details";

    /// <summary>RFC 8693 §4.1 — the actor claim on a delegated (composite-identity) token:
    /// <c>sub</c> is the user, <c>act.sub</c> is the agent acting for them, nesting one level
    /// per delegation hop.</summary>
    public const string Actor = "act";

    /// <summary>Token-endpoint parameter that resumes an exchange previously parked on
    /// <c>authorization_pending</c>: carries the id of the approval the user resolved.</summary>
    public const string ApprovalId = "approval_id";
}
