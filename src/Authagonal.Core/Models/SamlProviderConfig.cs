namespace Authagonal.Core.Models;

public sealed class SamlProviderConfig
{
    public required string ConnectionId { get; set; }
    public required string ConnectionName { get; set; }
    public required string EntityId { get; set; }
    public required string MetadataLocation { get; set; }
    public List<string> AllowedDomains { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
