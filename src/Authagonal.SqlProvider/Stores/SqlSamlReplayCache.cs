using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="ISamlReplayCache"/>. One table: pk = request/assertion id, sk = "request" |
/// "assertion". Request ids are consumed (delete-and-return) for single-use validation; assertion ids
/// are recorded with an insert-if-absent so a second sighting is detected as a replay.
/// <para>
/// Rows carry a TTL so the reaper clears them: an assertion id must stay recorded for at least as long
/// as the assertion could still be accepted, and no longer, or the table grows without bound. The
/// DynamoDB provider does NOT put a TTL on its replay table for exactly that first reason — expiring a
/// replay record early is a replay hole — so this is not a difference in diligence between the two.
/// </para>
/// </summary>
public sealed class SqlSamlReplayCache(SqlTable table, TimeSpan ttl) : ISamlReplayCache
{
    private const string RequestSk = "request";
    private const string AssertionSk = "assertion";

    public Task StoreRequestIdAsync(string requestId, string connectionId, CancellationToken ct = default)
        => StoreRequestAsync(requestId, connectionId, returnUrl: null, ct);

    public Task StoreRequestAsync(string requestId, string connectionId, string? returnUrl, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new SqlRow(requestId, RequestSk) { ExpiresAt = now.Add(ttl) };
        row.PutS("connectionId", connectionId);
        if (!string.IsNullOrEmpty(returnUrl)) row.PutS("returnUrl", returnUrl);
        row.PutDate("createdAt", now);
        return table.PutAsync(row, ct);
    }

    public async Task<string?> ValidateAndConsumeAsync(string requestId, CancellationToken ct = default)
        => (await ValidateAndConsumeRequestAsync(requestId, ct).ConfigureAwait(false))?.ConnectionId;

    public async Task<SamlRequestState?> ValidateAndConsumeRequestAsync(string requestId, CancellationToken ct = default)
    {
        var old = await table.DeleteIfExistsReturningAsync(requestId, RequestSk, ct).ConfigureAwait(false);
        if (old is null) return null;                                                  // not found / already consumed (replay)
        if (DateTimeOffset.UtcNow - old.GetDate("createdAt") > ttl) return null;        // expired
        var connectionId = old.GetS("connectionId");
        return connectionId is null ? null : new SamlRequestState(connectionId, old.GetS("returnUrl"));
    }

    public async Task<bool> CheckAndStoreAssertionIdAsync(
        string assertionId, DateTimeOffset? retainUntil = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Retention is the LATER of the default TTL and the assertion's own acceptability deadline. It used
        // to be the fixed TTL alone, and SqlExpiryReaper sweeps this table — so with an IdP whose assertions
        // outlive the TTL, the id was deleted while the assertion was still acceptable, and PutIfAbsentAsync
        // then reported the replay as a first sighting. This is the only provider that expired these rows:
        // the Azure and DynamoDB caches keep them indefinitely and were never exposed.
        var expires = now.Add(ttl);
        if (retainUntil is { } deadline && deadline > expires)
            expires = deadline;

        var row = new SqlRow(assertionId, AssertionSk) { ExpiresAt = expires };
        row.PutDate("createdAt", now);
        // Insert-if-absent: true only for the first sighting, so a replayed assertion loses the race
        // even when two nodes process it at the same moment.
        return await table.PutIfAbsentAsync(row, ct).ConfigureAwait(false);
    }
}
