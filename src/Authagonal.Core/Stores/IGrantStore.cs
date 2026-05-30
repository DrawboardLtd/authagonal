using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IGrantStore
{
    Task StoreAsync(PersistedGrant grant, CancellationToken ct = default);
    Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default);
    Task ConsumeAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes a single-use grant (e.g. an authorization code). Returns true only for the
    /// caller that actually removed it, so two concurrent redemptions of the same key cannot both
    /// succeed. Returns false if the grant was already gone / consumed by a racing caller.
    /// </summary>
    Task<bool> TryConsumeAsync(string key, CancellationToken ct = default);

    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
