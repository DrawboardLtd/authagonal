using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IOidcProviderStore
{
    Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default);
    Task DeleteAsync(string connectionId, CancellationToken ct = default);
}
