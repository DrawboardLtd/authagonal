namespace Authagonal.Core.Models;

public sealed class PersistedGrant
{
    public required string Key { get; set; }
    public required string Type { get; set; }
    public string? SubjectId { get; set; }
    public required string ClientId { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
