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

    /// <summary>
    /// Not read. The upstream <c>redirect_uri</c> is derived per request as
    /// <c>{issuer}/oidc/callback</c> — see <c>OidcEndpoints.CallbackUriFor</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It cannot be an input, and that is the reason rather than an oversight: the callback has to be on
    /// the origin the browser is actually on, so in a multi-tenant host no single stored value is correct
    /// for every tenant that shares a connection. Both legs of the flow therefore compute it, and they
    /// must compute the same thing — the upstream compares the authorize leg's <c>redirect_uri</c> with
    /// the token exchange's and rejects a mismatch.
    /// </para>
    /// <para>
    /// Every write path used to treat it as load-bearing anyway: the admin API answered 400 "RedirectUrl
    /// is required" without it and config seeding threw at startup, so an operator was made to supply a
    /// value, was given no validation of it, and got no indication that it did nothing. Neither refuses
    /// now, and a value that is not the derived one is logged as ignored.
    /// </para>
    /// <para>
    /// Retained rather than removed because three storage providers persist it, the admin DTO carries it
    /// and the Duende migration reader populates it — dropping the property would break stored rows and
    /// an API contract to delete a field that costs nothing.
    /// </para>
    /// </remarks>
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
    /// <c>sub</c>) rather than a fresh GUID. For a TRUSTED first-party connection (e.g. a share-link
    /// OIDC provider) this keeps the local <c>sub</c> equal to the downstream RP's own user
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
    /// (e.g. a guest share-link OIDC provider): federation still works, it just isn't offered as
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
    /// assertions are inbox-verified — e.g. a share-link provider, where the downstream host
    /// pre-creates the local account itself and possession of the emailed link is the verification.
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
    /// (e.g. the downstream provisioner asserts the federated email equals the invite recipient), since they are
    /// user-supplied on the authorize URL.
    /// </summary>
    public List<string> ProvisioningAttributeParams { get; set; } = [];

    /// <summary>
    /// When true, a JIT-provisioned federated user is created even WITHOUT the provisioning context that
    /// <see cref="ProvisioningAttributeParams"/> otherwise requires (an uninvited login from an allowed
    /// domain is auto-provisioned, not rejected). The connection is tagged onto the user's provisioning
    /// attributes (<c>federated_connection</c>) so the downstream provisioner can place the user in the
    /// right tenant rather than spinning up a new one. Default false: without an invite, an unknown user
    /// is rejected. Self-service SSO auto-provisioning opt-in. Mirrors the SAML flag of the same name.
    /// </summary>
    public bool AllowUninvitedJit { get; set; }

    /// <summary>
    /// Optional login-app path (e.g. <c>/guest</c>) shown BEFORE federating an unauthenticated
    /// <c>idp_hint</c> request through this connection. The authorize endpoint redirects to
    /// <c>{LoginAppUrl}{InteractionPath}?returnUrl={authorize url}&amp;connection={id}</c> instead of
    /// straight to <c>/oidc/{id}/login</c>; the page collects whatever the flow needs (a guest's
    /// name, terms consent), appends it to the returnUrl's query — where <see cref="PassthroughParams"/>
    /// and <see cref="ProvisioningAttributeParams"/> read from — and continues to the federation
    /// login URL itself. The host stays agnostic to what the page collects; a page that decides no
    /// interaction is needed can continue immediately. Null/empty = federate directly (default).
    /// </summary>
    public string? InteractionPath { get; set; }

    /// <summary>
    /// Revalidate the local session against this upstream IdP on every local refresh. Requests
    /// <c>offline_access</c> on the federation hop, stores the upstream refresh token in the local
    /// refresh grant, and redeems it server-to-server each time the local session refreshes — if the
    /// upstream rejects (<c>invalid_grant</c>), the local refresh is rejected too, so upstream
    /// revocation/expiry propagates within one access-token lifetime. Enable ONLY for a trusted
    /// first-party connection whose upstream owns a revocable credential (e.g. a guest share-link
    /// provider). Default false. Requires the upstream to actually issue a refresh token on the hop.
    /// </summary>
    public bool RevalidateOnRefresh { get; set; }

    /// <summary>
    /// Marks this as a THIRD-PARTY / external IdP (e.g. a customer's Entra/Okta) rather than a connection
    /// the operator controls. Default false (first-party) — existing connections are unaffected. When true,
    /// the first-party-only flags are NEUTRALISED even if set, so a misconfiguration can't hand an external
    /// upstream an account-takeover lever (choosing the local user id, or auto-linking by email).
    /// </summary>
    public bool IsExternalConnection { get; set; }

    /// <summary><see cref="UseUpstreamSubjectAsUserId"/>, honoured only on a first-party connection (not
    /// <see cref="IsExternalConnection"/>). Consume this at the call site, not the raw flag.</summary>
    public bool EffectiveUseUpstreamSubjectAsUserId => UseUpstreamSubjectAsUserId && !IsExternalConnection;

    /// <summary><see cref="AutoLinkExistingByEmail"/>, honoured only on a first-party connection (not
    /// <see cref="IsExternalConnection"/>). Consume this at the call site, not the raw flag.</summary>
    public bool EffectiveAutoLinkExistingByEmail => AutoLinkExistingByEmail && !IsExternalConnection;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
