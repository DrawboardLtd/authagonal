namespace Authagonal.Core.Models;

public sealed class ScimToken
{
    public required string TokenId { get; set; }
    public required string ClientId { get; set; }
    public required string TokenHash { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
}
