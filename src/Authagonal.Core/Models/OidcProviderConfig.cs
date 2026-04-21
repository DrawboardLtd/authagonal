namespace Authagonal.Core.Models;

public sealed class OidcProviderConfig
{
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string MetadataLocation { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUrl { get; set; } = "";
    public List<string> AllowedDomains { get; set; } = [];
    public bool DisableJitProvisioning { get; set; }

    /// <summary>
    /// Optional id_token claim name whose value (Unix seconds) sets the maximum lifetime of
    /// the local session established after federation. Propagates into issued refresh tokens
    /// so they cannot outlive the upstream IdP session.
    /// </summary>
    public string? SessionExpClaim { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
