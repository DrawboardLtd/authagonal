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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
