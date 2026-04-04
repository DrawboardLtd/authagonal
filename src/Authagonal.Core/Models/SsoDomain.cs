namespace Authagonal.Core.Models;

public sealed class SsoDomain
{
    public required string Domain { get; set; }
    public required string ProviderType { get; set; }
    public required string ConnectionId { get; set; }
    public required string Scheme { get; set; }
}
