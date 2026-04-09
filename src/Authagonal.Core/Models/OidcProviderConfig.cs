namespace Authagonal.Core.Models;

public sealed class OidcProviderConfig
{
    public required string ConnectionId { get; set; }
    public required string ConnectionName { get; set; }
    public required string MetadataLocation { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public required string RedirectUrl { get; set; }
    public List<string> AllowedDomains { get; set; } = [];
    public bool DisableJitProvisioning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
