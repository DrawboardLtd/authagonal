namespace Authagonal.Core.Models;

public sealed class SamlProviderConfig
{
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    /// <summary>Optional icon URL shown on the "Continue with {name}" login button.</summary>
    public string? IconUrl { get; set; }
    public string EntityId { get; set; } = "";
    public string MetadataLocation { get; set; } = "";
    /// <summary>
    /// Pasted IdP metadata XML, for IdPs that publish no metadata URL (Google Workspace) or whose
    /// metadata endpoint is unreachable from the SP (private-network ADFS). When set it is used
    /// instead of fetching <see cref="MetadataLocation"/>. Stores expect the condensed canonical
    /// form produced at write time, not the raw multi-hundred-KB vendor document.
    /// </summary>
    public string? MetadataXml { get; set; }
    /// <summary>
    /// NameIDPolicy Format requested in the AuthnRequest. Null = the emailAddress format (historic
    /// default). "none" = omit NameIDPolicy entirely — the safe setting for ADFS, which fails the
    /// whole login (MSIS7070) when its claim rules don't emit the requested format. Any other value
    /// is sent verbatim as the Format URN.
    /// </summary>
    public string? NameIdFormat { get; set; }
    /// <summary>
    /// SP keypair as base64 PKCS#12 (no password), protected at rest via the host's secret provider.
    /// Enables EncryptedAssertion decryption (ADFS default when the SP advertises an encryption cert),
    /// signed AuthnRequests, and signed logout messages. Auto-generated at connection creation;
    /// server-only — never returned to API callers.
    /// </summary>
    public string? SpCertificate { get; set; }
    /// <summary>
    /// Force AuthnRequest signing (redirect-binding SigAlg/Signature). Null/false = sign only when
    /// the IdP's metadata declares WantAuthnRequestsSigned.
    /// </summary>
    public bool? SignAuthnRequests { get; set; }

    /// <summary>
    /// Whether this connection accepts an IdP-initiated (unsolicited) Response — one carrying no
    /// <c>InResponseTo</c>, for which this server issued no AuthnRequest. Defaults to OFF.
    /// </summary>
    /// <remarks>
    /// An unsolicited response cannot be tied to a pending request or to a browser, so accepting one means
    /// anyone holding a valid assertion from this IdP can establish that subject's session here from any
    /// user-agent — including an assertion the attacker obtained legitimately for their OWN account at the
    /// same IdP, which makes it a login-CSRF primitive: every other §4.1.4.3 rule (Issuer, Destination,
    /// Recipient, Audience, signature, first-sighting of the assertion id) is satisfied by that assertion.
    /// <para>
    /// It also decides whether the browser binding below can be relied on at all. Requiring the
    /// AuthnRequest-id cookie on the SP-initiated path is worth nothing while the same assertion can be
    /// replayed with <c>InResponseTo</c> simply removed.
    /// </para>
    /// <para>
    /// The profile permits IdP-initiated SSO, so this is a deployment decision — and therefore one an
    /// operator makes deliberately, per connection, rather than one every connection has by default.
    /// </para>
    /// </remarks>
    public bool AllowUnsolicitedResponses { get; set; }

    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>
    /// Whether an unknown federated user is created on first login (just-in-time). Defaults to OFF:
    /// a connection must explicitly opt in, otherwise an unknown assertion is rejected.
    /// </summary>
    public bool JitProvisioningEnabled { get; set; }

    /// <summary>Inverted alias for <see cref="JitProvisioningEnabled"/>, retained for back-compat with
    /// existing config keys, admin DTOs and stored rows (which persist the negative form).</summary>
    public bool DisableJitProvisioning { get => !JitProvisioningEnabled; set => JitProvisioningEnabled = !value; }

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
    /// Whitelisted query parameters from the original /authorize request (carried on the SP-initiated
    /// return URL) captured as provisioning CustomAttributes on a JIT-provisioned federated user, so
    /// downstream provisioning (the TCC callback) sees them. Lets an SSO user complete an org invite
    /// through the same provisioning pipeline as a password signup (invite context e.g. <c>acceptKind</c>,
    /// <c>acceptToken</c>). Empty means nothing is captured; the downstream provisioner is the security
    /// gate (they are user-supplied on the authorize URL).
    /// </summary>
    public List<string> ProvisioningAttributeParams { get; set; } = [];

    /// <summary>
    /// When true, a JIT-provisioned federated user is created even WITHOUT the provisioning context
    /// that <see cref="ProvisioningAttributeParams"/> otherwise requires (i.e. an uninvited login from an
    /// allowed domain is auto-provisioned, not rejected). The connection identity is tagged onto the
    /// user's provisioning attributes (<c>federated_connection</c>) so the downstream provisioner can
    /// place the user in the right tenant rather than spinning up a new one. Default false: without an
    /// invite, an unknown user is rejected. Self-service SSO auto-provisioning opt-in.
    /// </summary>
    public bool AllowUninvitedJit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
