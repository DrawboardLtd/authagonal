namespace Authagonal.Core.Models;

public sealed class SamlProviderConfig
{
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string MetadataLocation { get; set; } = "";
    public List<string> AllowedDomains { get; set; } = [];
    public bool DisableJitProvisioning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
