namespace Authagonal.Core.Models;

public sealed class SigningKeyInfo
{
    public required string KeyId { get; set; }
    public required string Algorithm { get; set; }
    public required string RsaParametersJson { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
