namespace Authagonal.Core.Models;

public sealed class ScimGroup
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public string? ExternalId { get; set; }
    public string? OrganizationId { get; set; }
    public List<string> MemberUserIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
