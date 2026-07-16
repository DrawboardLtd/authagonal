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
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>
    /// Whether an unknown federated user is created on first login (just-in-time). Defaults to OFF:
    /// a connection must explicitly opt in, otherwise an unknown assertion is rejected.
    /// </summary>
    public bool JitProvisioningEnabled { get; set; }

    /// <summary>Inverted alias for <see cref="JitProvisioningEnabled"/>, retained for back-compat with
    /// existing config keys, admin DTOs and stored rows (which persist the negative form).</summary>
    public bool DisableJitProvisioning { get => !JitProvisioningEnabled; set => JitProvisioningEnabled = !value; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
