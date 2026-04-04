using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface ISsoDomainStore
{
    Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default);
    Task<IReadOnlyList<SsoDomain>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(SsoDomain domain, CancellationToken ct = default);
    Task DeleteAsync(string domain, CancellationToken ct = default);
    Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default);
}
