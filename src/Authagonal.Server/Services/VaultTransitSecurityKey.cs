using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// Lightweight SecurityKey that holds the Vault Transit key name and ECDSA P-256 public
/// key parameters. No private key material — signing happens remotely via Vault Transit.
/// Verification is done locally with the cached public key to avoid a Vault round-trip
/// on every JWT validation.
/// </summary>
public sealed class VaultTransitSecurityKey : SecurityKey
{
    public string VaultKeyName { get; }
    public int KeyVersion { get; }
    public ECParameters PublicKeyParameters { get; }
    public VaultTransitClient TransitClient { get; }

    public override string KeyId { get; set; }
    public override int KeySize => 256;

    public VaultTransitSecurityKey(
        VaultTransitClient transitClient,
        string vaultKeyName,
        int keyVersion,
        string keyId,
        ECParameters publicKeyParameters)
    {
        TransitClient = transitClient;
        VaultKeyName = vaultKeyName;
        KeyVersion = keyVersion;
        KeyId = keyId;
        PublicKeyParameters = publicKeyParameters;
    }
}
