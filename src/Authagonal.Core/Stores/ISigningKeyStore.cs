using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface ISigningKeyStore
{
    Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default);
    Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default);
    Task DeactivateKeyAsync(string keyId, CancellationToken ct = default);
    Task DeleteAsync(string keyId, CancellationToken ct = default);
}
