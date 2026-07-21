namespace Authagonal.Core.Models;

public sealed record OidcProviderConfig
{
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    /// <summary>Optional icon URL shown on the "Continue with {name}" login button.</summary>
    public string? IconUrl { get; set; }
    public string MetadataLocation { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUrl { get; set; } = "";
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>
    /// Whether an unknown federated user is created on first login (just-in-time). Defaults to OFF:
    /// a connection must explicitly opt in, otherwise an unknown assertion is rejected. Safer posture,
    /// and honest in config (<c>"JitProvisioningEnabled": true</c> turns it on).
    /// </summary>
    public bool JitProvisioningEnabled { get; set; }

    /// <summary>
    /// Inverted alias for <see cref="JitProvisioningEnabled"/>, retained for back-compat with existing
    /// config keys, admin DTOs and stored rows/blobs (which persist the negative form). Prefer the
    /// positive property in new code.
    /// </summary>
    public bool DisableJitProvisioning { get => !JitProvisioningEnabled; set => JitProvisioningEnabled = !value; }

    /// <summary>
    /// When true, a JIT-provisioned federated user's local id is the upstream subject (id_token
    /// <c>sub</c>) rather than a fresh GUID. For a TRUSTED first-party connection (e.g. bullclip's
    /// guest-link OIDC provider) this keeps the local <c>sub</c> equal to the downstream RP's own user
    /// id, so identifiers like a share link's ClaimedByUserId stay consistent. Do NOT enable for
    /// arbitrary external IdPs — it lets the upstream choose the local user id.
    /// </summary>
    public bool UseUpstreamSubjectAsUserId { get; set; }

    /// <summary>
    /// Optional id_token claim name whose value (Unix seconds) sets the maximum lifetime of
    /// the local session established after federation. Propagates into issued refresh tokens
    /// so they cannot outlive the upstream IdP session.
    /// </summary>
    public string? SessionExpClaim { get; set; }

    /// <summary>
    /// Whitelisted query parameters that flow through from the original /authorize
    /// request onto the upstream IdP's authorize URL. Supports use cases like share-link
    /// federation, where a one-shot credential (e.g. <c>link_token</c>) carried by the
    /// downstream RP needs to reach the upstream IdP's authentication handler. Empty
    /// means nothing custom passes through — the standard scope/state/nonce/PKCE set
    /// is always forwarded regardless.
    /// </summary>
    public List<string> PassthroughParams { get; set; } = [];

    /// <summary>
    /// Whether this connection is advertised as a "Continue with {name}" button on the login page.
    /// Defaults to true. Set false for a connection reached only via an explicit <c>idp_hint</c>
    /// (e.g. bullclip's guest-link OIDC provider): federation still works, it just isn't offered as
    /// a button — a bounded share-link credential is not something a user picks from the login form.
    /// </summary>
    public bool ShowOnLogin { get; set; } = true;

    /// <summary>
    /// Inverted alias for <see cref="ShowOnLogin"/>, persisted in the negative form so an existing
    /// stored connection with no column reads back as shown (the safe default). Prefer the positive
    /// property in new code/config.
    /// </summary>
    public bool HiddenFromLogin { get => !ShowOnLogin; set => ShowOnLogin = !value; }

    /// <summary>
    /// Whether a user is still routed through the LOCAL MFA challenge after this connection
    /// authenticates them (F42). Default true: a federated assertion proves only the first factor.
    /// Set false when the tenant trusts the upstream IdP to enforce its own MFA — the local
    /// challenge is skipped and the session is signed in as mfa-authenticated on federation alone.
    /// </summary>
    public bool ChallengeMfaAfterLogin { get; set; } = true;

    /// <summary>Inverted persistence alias for <see cref="ChallengeMfaAfterLogin"/> — stored rows
    /// written before this field existed read false → challenge stays ON (the safe default).</summary>
    public bool SkipMfaAfterFederatedLogin { get => !ChallengeMfaAfterLogin; set => ChallengeMfaAfterLogin = !value; }

    /// <summary>
    /// Link a federated identity to an EXISTING local account matched by email even when the
    /// connection's AllowedDomains does not vouch for the email's domain. Default false (the
    /// anti-takeover stance). Enable ONLY for a trusted first-party connection whose email
    /// assertions are inbox-verified — e.g. a share-link provider, where bullclip pre-creates the
    /// local account itself and possession of the emailed link is the verification.
    /// </summary>
    public bool AutoLinkExistingByEmail { get; set; }

    /// <summary>
    /// Whitelisted query parameters from the original /authorize request that are captured as
    /// provisioning CustomAttributes on a JIT-provisioned federated user, so downstream provisioning
    /// (the TCC callback) sees them. The mirror image of <see cref="PassthroughParams"/>: those flow
    /// OUTWARD to the upstream IdP, these flow INWARD to provisioning. Enables an SSO user to complete
    /// an org invite through the same provisioning pipeline as a password signup — the invite context
    /// (e.g. <c>acceptKind</c>, <c>acceptToken</c>) rides the authorize URL and lands on the user.
    /// Empty means nothing is captured. The downstream provisioner is the security gate on these
    /// (e.g. bullclip asserts the federated email equals the invite recipient), since they are
    /// user-supplied on the authorize URL.
    /// </summary>
    public List<string> ProvisioningAttributeParams { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
