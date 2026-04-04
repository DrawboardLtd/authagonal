using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface ISamlProviderStore
{
    Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default);
    Task DeleteAsync(string connectionId, CancellationToken ct = default);
}
