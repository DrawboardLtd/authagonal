namespace Authagonal.Core.Models;

public sealed class SigningKeyInfo
{
    public required string KeyId { get; set; }
    public required string Algorithm { get; set; }

    /// <summary>JSON-encoded private key material. Shape depends on <see cref="Algorithm"/>:
    /// EC keys hold <c>D</c>, <c>QX</c>, <c>QY</c>, <c>Curve</c>; legacy RSA holds the
    /// usual modulus/exponent set.</summary>
    public required string KeyMaterialJson { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
