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

    /// <summary>
    /// Atomically records the consumed marker (<see cref="PersistedGrant.ConsumedAt"/> plus the supplied
    /// <see cref="PersistedGrant.Data"/> — which for refresh rotation carries the successor key) for a
    /// rotating / single-use grant, but ONLY if the grant is currently un-consumed. Returns true for the
    /// one caller that won the transition; false if a concurrent caller already consumed it (the loser
    /// must re-read and treat it as replay / grace-window reuse). Unlike <see cref="TryConsumeAsync"/>
    /// this KEEPS the row — rotation needs the consumed marker for replay detection and the grace-window
    /// reissue. The caller must set <see cref="PersistedGrant.Key"/> (grants read back from storage have
    /// none), or the write would land under the wrong (empty-key) partition.
    /// </summary>
    Task<bool> TryMarkConsumedAsync(PersistedGrant grant, CancellationToken ct = default);

    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default);

    /// <summary>
    /// Removes the subject's grants whose <see cref="PersistedGrant.Type"/> appears in
    /// <paramref name="types"/>, optionally narrowed to a single <paramref name="clientId"/>.
    /// </summary>
    /// <remarks>
    /// Exists because the two bulk removals above are both too coarse for the callers that need them.
    /// Ending a sign-in session must drop the session's token authority without deleting the user's
    /// recorded consent, and revoking one authorized app must drop that app's refresh tokens without
    /// touching every other client's. Both were reaching for
    /// <see cref="RemoveAllBySubjectAsync"/> and destroying far more than they meant to. See
    /// <see cref="Authagonal.Core.Constants.PersistedGrantTypes.SessionBound"/>.
    /// </remarks>
    Task RemoveBySubjectAsync(
        string subjectId,
        IReadOnlyCollection<string> types,
        string? clientId = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
