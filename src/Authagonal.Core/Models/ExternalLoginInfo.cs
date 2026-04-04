namespace Authagonal.Core.Models;

public sealed class ExternalLoginInfo
{
    public required string UserId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderKey { get; set; }
    public string? DisplayName { get; set; }
}
