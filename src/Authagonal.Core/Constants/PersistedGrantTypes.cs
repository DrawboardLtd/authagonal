namespace Authagonal.Core.Constants;

/// <summary>
/// The <see cref="Authagonal.Core.Models.PersistedGrant.Type"/> discriminators.
/// </summary>
/// <remarks>
/// Distinct from <see cref="GrantTypes"/>, which holds the OAuth <c>grant_type</c> request values. These
/// are storage discriminators, and they were previously bare literals at every call site — which is how
/// the end-session handler came to delete every type indiscriminately. Naming them makes the blast radius
/// of a bulk removal something you can read.
/// </remarks>
public static class PersistedGrantTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string RefreshToken = "refresh_token";
    public const string DeviceCode = "device_code";
    public const string PushedAuthorizationRequest = "pushed_authorization_request";

    /// <summary>Recorded end-user consent for a client. A long-lived preference, not session state.</summary>
    public const string Consent = "consent";

    /// <summary>Recorded end-user consent for an agent. Long-lived, as <see cref="Consent"/>.</summary>
    public const string AgentConsent = "agent_consent";

    public const string Approval = "approval";
    public const string CapabilityTicket = "capability_ticket";

    /// <summary>
    /// The grant types that carry live token authority for a sign-in session: removing these ends the
    /// session's ability to obtain tokens. Ending a session removes these and nothing else — deleting a
    /// user's recorded consent is revocation, which is a separate, deliberate act with its own UI.
    /// </summary>
    public static readonly IReadOnlyCollection<string> SessionBound =
        [RefreshToken, AuthorizationCode, DeviceCode, PushedAuthorizationRequest];
}
