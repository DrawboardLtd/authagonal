namespace Authagonal.Core.Models;

public sealed class UserProvision
{
    public required string UserId { get; set; }
    public required string AppId { get; set; }
    public DateTimeOffset ProvisionedAt { get; set; }
}
