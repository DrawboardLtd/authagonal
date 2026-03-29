namespace Authagonal.Core.Models;

public sealed class MfaCredential
{
    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required MfaCredentialType Type { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// TOTP: encrypted secret via ISecretProvider. Recovery: SHA256 hash of the code.
    /// </summary>
    public string? SecretProtected { get; set; }

    /// <summary>
    /// WebAuthn: JSON-serialized credential data (credentialId, publicKey, credType, aaguid).
    /// </summary>
    public string? PublicKeyJson { get; set; }

    /// <summary>WebAuthn clone detection counter.</summary>
    public uint SignCount { get; set; }

    /// <summary>Recovery codes only: true once the code has been used.</summary>
    public bool IsConsumed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum MfaCredentialType
{
    Totp = 0,
    WebAuthn = 1,
    RecoveryCode = 2
}
