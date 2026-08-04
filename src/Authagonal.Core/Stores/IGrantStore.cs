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

    /// <summary>
    /// Atomically replaces a grant's <see cref="PersistedGrant.Data"/>, but ONLY if the grant still exists
    /// and is NOT consumed. Returns false for a caller whose row was consumed or deleted in the meantime.
    /// </summary>
    /// <remarks>
    /// The read-modify-write sibling of <see cref="TryMarkConsumedAsync"/>, for the one caller that has to
    /// amend a LIVE grant it already read: the refresh grace window appends the access-token jti it just
    /// minted to the successor's tracked list, so revoking that refresh token can still reach the token.
    /// <para>
    /// It used to do that with <see cref="StoreAsync"/> — an unconditional full-row upsert on every provider.
    /// The instance written carries <c>ConsumedAt = null</c>, and on DynamoDB and SQL the write also drops the
    /// top-level <c>consumedAt</c> guard attribute that <see cref="TryMarkConsumedAsync"/> conditions on. So a
    /// consume or delete landing between the read and the write was silently undone: a revoked grant came
    /// back, and rotation-replay detection stopped seeing the marker it depends on.
    /// </para>
    /// <para>
    /// The caller must set <see cref="PersistedGrant.Key"/> — grants read back from storage have none — or the
    /// write would land under the empty-key partition.
    /// </para>
    /// </remarks>
    Task<bool> TryUpdateDataIfUnconsumedAsync(PersistedGrant grant, CancellationToken ct = default);

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
    /// <summary>
    /// Removes the subject's grants of the given <paramref name="types"/> that belong to one sign-in
    /// session — or, when <paramref name="invert"/> is true, to every session EXCEPT that one. Returns the
    /// number of grant rows removed.
    /// </summary>
    /// <param name="sessionId">The session to match on <see cref="PersistedGrant.SessionId"/>.</param>
    /// <param name="invert">
    /// False: remove the grants belonging to <paramref name="sessionId"/> ("log this device out").
    /// True: remove the grants belonging to any other session ("log my other devices out").
    /// </param>
    /// <remarks>
    /// The narrowest of the three bulk removals, and the one the self-service session list needs. Ending a
    /// session used to be expressible only as subject-wide or subject-and-client-wide, so the account page's
    /// "Log out other devices" deleted the <c>Sessions</c> row and nothing else: the refresh token the
    /// relying party on the stolen laptop already held kept rotating for the whole absolute refresh lifetime,
    /// while the user had been told every other device was signed out. Reaching for
    /// <see cref="RemoveBySubjectAsync"/> instead would have killed the tokens on the device they chose to
    /// keep.
    /// <para>
    /// A grant whose <see cref="PersistedGrant.SessionId"/> is null is never matched, in either direction.
    /// It cannot be attributed to the session being ended, so ending that session must not destroy it —
    /// which also makes this safe against rows written before the column existed.
    /// </para>
    /// </remarks>
    Task<int> RemoveBySessionAsync(
        string subjectId,
        IReadOnlyCollection<string> types,
        string sessionId,
        bool invert = false,
        CancellationToken ct = default);

    Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default);
    Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
