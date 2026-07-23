using System.Text.Json;

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

    /// <summary>TOTP only: the last time-step a code was accepted at, to reject replay of an
    /// already-used code within its validity window. Null until the first successful verification.</summary>
    public long? LastTotpStep { get; set; }

    /// <summary>Recovery codes only: true once the code has been used.</summary>
    public bool IsConsumed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Extract the raw WebAuthn credential id (the value the credential-id lookup index is keyed on) from
    /// <see cref="PublicKeyJson"/>. Returns false — never throws — for a non-WebAuthn factor, a
    /// missing/blank PublicKeyJson, or malformed JSON/base64, so a caller can skip index cleanup on bad
    /// data while leaving real storage faults to surface. Shared by the Azure and Dynamo MFA stores.
    /// </summary>
    public bool TryGetWebAuthnCredentialId(out byte[] credentialId)
    {
        credentialId = [];
        if (Type != MfaCredentialType.WebAuthn || string.IsNullOrEmpty(PublicKeyJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(PublicKeyJson);
            string? b64 = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, "credentialId", StringComparison.OrdinalIgnoreCase))
                {
                    b64 = prop.Value.GetString();
                    break;
                }
            if (string.IsNullOrEmpty(b64))
                return false;
            credentialId = Convert.FromBase64String(b64);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }
}

public enum MfaCredentialType
{
    Totp = 0,
    WebAuthn = 1,
    RecoveryCode = 2
}
