using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IGrantStore
{
    Task StoreAsync(PersistedGrant grant, CancellationToken ct = default);
    Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default);
    Task ConsumeAsync(string key, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
