using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface ISigningKeyStore
{
    Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SigningKeyInfo>> GetAllKeysAsync(CancellationToken ct = default);
    Task StoreKeyAsync(SigningKeyInfo key, CancellationToken ct = default);
    Task DeactivateKeyAsync(string keyId, CancellationToken ct = default);
    Task DeleteKeyAsync(string keyId, CancellationToken ct = default);
}
