using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// Custom ICryptoProvider that intercepts signing requests for VaultTransitSecurityKey
/// and delegates to VaultTransitSignatureProvider. Registered on CryptoProviderFactory
/// so that JsonWebTokenHandler uses Vault Transit for signing automatically.
/// </summary>
public sealed class VaultTransitCryptoProvider : ICryptoProvider
{
    public bool IsSupportedAlgorithm(string algorithm, params object[] args)
    {
        return algorithm == SecurityAlgorithms.RsaSha256
            && args.Length > 0
            && args[0] is VaultTransitSecurityKey;
    }

    public object Create(string algorithm, params object[] args)
    {
        if (args[0] is VaultTransitSecurityKey key)
            return new VaultTransitSignatureProvider(key, algorithm);

        throw new NotSupportedException($"Cannot create provider for {args[0]?.GetType().Name}");
    }

    public void Release(object cryptoInstance)
    {
        if (cryptoInstance is IDisposable disposable)
            disposable.Dispose();
    }
}
