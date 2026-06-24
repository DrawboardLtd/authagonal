namespace Authagonal.Core.Models;

public sealed class SamlProviderConfig
{
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    /// <summary>Optional icon URL shown on the "Continue with {name}" login button.</summary>
    public string? IconUrl { get; set; }
    public string EntityId { get; set; } = "";
    public string MetadataLocation { get; set; } = "";
    public List<string> AllowedDomains { get; set; } = [];
    public bool DisableJitProvisioning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
