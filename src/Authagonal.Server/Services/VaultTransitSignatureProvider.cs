using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// SignatureProvider that delegates ES256 signing to HashiCorp Vault Transit.
/// Private key never leaves Vault. Vault is asked for JWS-marshaled signatures
/// (raw R‖S, each 32 bytes) so the bytes can be returned directly to
/// <see cref="System.IdentityModel.Tokens.Jwt"/> without DER→P1363 conversion.
/// Verification uses the cached public key locally to avoid a round-trip.
/// </summary>
public sealed class VaultTransitSignatureProvider : SignatureProvider
{
    private readonly VaultTransitSecurityKey _key;

    public VaultTransitSignatureProvider(VaultTransitSecurityKey key, string algorithm)
        : base(key, algorithm)
    {
        _key = key;
    }

    public override byte[] Sign(byte[] input)
    {
        return _key.TransitClient.SignAsync(_key.VaultKeyName, input, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public override bool Sign(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten)
    {
        var sig = Sign(data.ToArray());
        if (sig.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }
        sig.CopyTo(destination);
        bytesWritten = sig.Length;
        return true;
    }

    public override bool Verify(byte[] input, byte[] signature)
    {
        using var ecdsa = ECDsa.Create(_key.PublicKeyParameters);
        return ecdsa.VerifyData(input, signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public override bool Verify(byte[] input, int inputOffset, int inputLength,
        byte[] signature, int signatureOffset, int signatureLength)
    {
        var inputSpan = input.AsSpan(inputOffset, inputLength).ToArray();
        var sigSpan = signature.AsSpan(signatureOffset, signatureLength).ToArray();
        return Verify(inputSpan, sigSpan);
    }

    protected override void Dispose(bool disposing) { }
}
