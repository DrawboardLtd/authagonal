using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IClientStore
{
    Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default);
    Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(OAuthClient client, CancellationToken ct = default);
    Task DeleteAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites ONE entry of <see cref="OAuthClient.ClientSecretHashes"/> to a stronger format, but only
    /// while the stored entry is still <paramref name="expectedHash"/> and nothing else about the record has
    /// changed. Returns false — skip the upgrade — when the record moved underneath, or when the backend has
    /// no conditional-write primitive.
    /// </summary>
    /// <remarks>
    /// Not source-breaking: the default returns false, so an external implementer keeps compiling and simply
    /// leaves legacy hashes on their legacy format (which <c>LegacySecretHashWarning</c> already reports).
    /// <para>
    /// The upgrade previously read the record, mutated the hash list, and wrote the WHOLE record back with an
    /// unconditional upsert. It compared only the entry at <c>index</c> as it stood at the re-read, so any
    /// administrative write landing between that read and the write was reverted wholesale — the hash list
    /// (undoing a secret rotation, so a rotated-out secret kept working), <c>Enabled</c> (re-enabling a client
    /// an admin had just disabled), scopes, redirect URIs. The losing write was always the administrator's, and
    /// the attacker did not need to observe the rotation: the throttle permits 30 authentications per minute
    /// per client, so holding the endpoint open covers the window. This is the same defect class the
    /// <c>TableUserStore.RecordSuccessfulLoginAsync</c> fix documents — "an attacker who keeps authenticating
    /// controls one side of that race" — on the client record.
    /// </para>
    /// <para>
    /// Implement with the backend's own conditional primitive (ETag, version compare-and-set, conditional
    /// expression) and return false on a lost race rather than retrying: authentication has already succeeded,
    /// so a skipped upgrade costs nothing but a repeat attempt on the next call.
    /// </para>
    /// <para>
    /// <b>Current coverage.</b> Azure Table implements it (ETag CAS over the whole row, so a concurrent change
    /// to any column loses the race). The SQL and DynamoDB client stores do NOT: their rows carry no version
    /// attribute, and adding one means maintaining it on every write path — so they stay on this default and
    /// leave legacy hashes in place, which is strictly safer than the unconditional write they did before but
    /// is not feature parity. <c>LegacySecretHashWarning</c> is what surfaces the remaining legacy hashes to an
    /// operator on those backends.
    /// </para>
    /// </remarks>
    Task<bool> TryUpgradeSecretHashAsync(
        string clientId, int index, string expectedHash, string newHash, CancellationToken ct = default)
        => Task.FromResult(false);
}
