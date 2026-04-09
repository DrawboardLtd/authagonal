using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// SignatureProvider that delegates signing to HashiCorp Vault Transit.
/// Private key never leaves Vault — signing happens remotely via the Transit API.
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
        // Verify locally using the cached public key — no need to call Vault
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportParameters(_key.PublicKeyParameters);
        return rsa.VerifyData(input, signature,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
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
