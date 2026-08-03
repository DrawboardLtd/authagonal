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

    /// <summary>
    /// What the authorize endpoint is currently OFFERING a subject for a client — written immediately
    /// before the user-agent is redirected to the consent screen, and consumed by the consent POST.
    /// </summary>
    /// <remarks>
    /// Short-lived and single-use. It is what makes the consent screen a view of a pending authorization
    /// request rather than a function of the link that was clicked: without a live record, the screen has
    /// nothing to render and the POST has no offer to accept.
    /// </remarks>
    public const string ConsentOffer = "consent_offer";

    /// <summary>
    /// A one-shot marker that the consent screen has just been shown and answered, so a
    /// <c>prompt=consent</c> authorization request does not demand it a second time.
    /// </summary>
    /// <remarks>
    /// The consent POST sends the user-agent back to the same authorize URL, <c>prompt</c> included. Without
    /// a record that the demand was satisfied, honouring <c>prompt=consent</c> is an infinite redirect loop
    /// between the two endpoints.
    /// </remarks>
    public const string ConsentPrompt = "consent_prompt";

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
