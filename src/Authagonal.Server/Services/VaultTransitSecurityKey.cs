using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// Lightweight SecurityKey that holds the Vault Transit key name and public key.
/// No private key material — signing is done remotely via Vault Transit.
/// </summary>
public sealed class VaultTransitSecurityKey : SecurityKey
{
    public string VaultKeyName { get; }
    public int KeyVersion { get; }
    public RSAParameters PublicKeyParameters { get; }
    public VaultTransitClient TransitClient { get; }

    public override string KeyId { get; set; }
    public override int KeySize => 2048;

    public VaultTransitSecurityKey(
        VaultTransitClient transitClient,
        string vaultKeyName,
        int keyVersion,
        string keyId,
        RSAParameters publicKeyParameters)
    {
        TransitClient = transitClient;
        VaultKeyName = vaultKeyName;
        KeyVersion = keyVersion;
        KeyId = keyId;
        PublicKeyParameters = publicKeyParameters;
    }
}
