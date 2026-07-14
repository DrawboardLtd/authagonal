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
    public List<string> AllowedDomains { get; set; } = [];
    public bool DisableJitProvisioning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
