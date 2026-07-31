using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Authagonal.Bff;

/// <summary>Default <see cref="IBffSessionStore"/> over <c>IDistributedCache</c>. Sessions are keyed by
/// their opaque id; secondary indexes map each subject (and each OIDC <c>sid</c>, when present) to the
/// set of session ids sharing it, so a back-channel logout can find and kill them.</summary>
internal sealed class DistributedCacheBffSessionStore(IDistributedCache cache) : IBffSessionStore
{
    private static string SessKey(string id) => $"agbff:sess:{id}";

    /// <summary>
    /// Secondary-index keys, namespaced by tenant.
    /// </summary>
    /// <remarks>
    /// These were keyed on the bare sid/sub. `sub` is only unique WITHIN an issuer, so on a
    /// multi-tenant BFF two tenants' users could share one — and a back-channel logout accepted from
    /// tenant A's IdP then terminated tenant B's sessions for the colliding subject. Since the logout
    /// endpoint is reachable by anyone holding a valid token from ANY configured tenant, that is a
    /// cross-tenant denial of service. Namespacing makes the index mean what its name says: this
    /// subject, at this issuer.
    /// </remarks>
    private static string SidKey(string? tenantKey, string sid) => $"agbff:sid:{tenantKey ?? "-"}:{sid}";

    private static string SubKey(string? tenantKey, string sub) => $"agbff:sub:{tenantKey ?? "-"}:{sub}";

    /// <summary>
    /// Kill markers: the instant every session for a subject (or sid) was revoked.
    /// </summary>
    /// <remarks>
    /// The roster indexes above are JSON blobs maintained by load-mutate-store, and IDistributedCache
    /// offers neither compare-and-swap nor set operations — so two interleaved writers lose an entry,
    /// and SetAsync runs on every login AND every token refresh, which makes that ordinary rather than
    /// exotic. A lost entry fails OPEN: the session is invisible to back-channel logout and to "sign
    /// out everywhere", and survives with live access and refresh tokens for the whole session
    /// lifetime, with nothing to detect or repair it because every repair path consults the same
    /// index.
    ///
    /// A marker is a single-key write with no read-modify-write, so it cannot be lost — and a session
    /// established before it is dead on the next read whether or not the roster ever knew about it.
    /// The rosters stay, for eager cleanup and for the count the logout endpoint logs, but nothing
    /// depends on them being complete.
    /// </remarks>
    private static string SubKillKey(string? tenantKey, string sub) => $"agbff:kill:sub:{tenantKey ?? "-"}:{sub}";

    private static string SidKillKey(string? tenantKey, string sid) => $"agbff:kill:sid:{tenantKey ?? "-"}:{sid}";

    /// <summary>
    /// How long a kill marker is kept. It only has to outlive any session that could have been
    /// established before it — the BFF's absolute session lifetime, which defaults to 8 hours — so a
    /// week is a wide margin over any plausible configuration.
    /// </summary>
    private static readonly TimeSpan KillMarkerRetention = TimeSpan.FromDays(7);

    public async Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(SessKey(sessionId), ct);
        if (json is null) return null;

        var session = JsonSerializer.Deserialize(json, BffJsonContext.Default.BffSession);
        if (session is null) return null;

        // One or two extra reads on the session-load path, deliberately. They are what make a
        // back-channel logout actually terminate a session rather than terminate the sessions the
        // roster happens to remember.
        if (await IsRevokedAsync(session, ct))
        {
            await cache.RemoveAsync(SessKey(sessionId), ct);
            return null;
        }

        return session;
    }

    private async Task<bool> IsRevokedAsync(BffSession session, CancellationToken ct)
    {
        if (await KilledAfterAsync(SubKillKey(session.TenantKey, session.Subject), session.CreatedAt, ct))
            return true;

        return session.Sid is not null
            && await KilledAfterAsync(SidKillKey(session.TenantKey, session.Sid), session.CreatedAt, ct);
    }

    private async Task<bool> KilledAfterAsync(string killKey, DateTimeOffset createdAt, CancellationToken ct)
    {
        var marker = await cache.GetStringAsync(killKey, ct);
        return marker is not null
            && long.TryParse(marker, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            && createdAt.UtcTicks <= ticks;
    }

    public async Task SetAsync(BffSession session, CancellationToken ct = default)
    {
        // Stamped once, at establishment. SetAsync also runs on every token refresh, and re-stamping
        // there would let a refresh carry a session past a kill marker — which is precisely the
        // revocation this is here to enforce.
        if (session.CreatedAt == default)
            session.CreatedAt = DateTimeOffset.UtcNow;

        var opts = new DistributedCacheEntryOptions { AbsoluteExpiration = session.ExpiresAt };
        await cache.SetStringAsync(
            SessKey(session.SessionId),
            JsonSerializer.Serialize(session, BffJsonContext.Default.BffSession),
            opts, ct);

        await AddToIndexAsync(SubKey(session.TenantKey, session.Subject), session.SessionId, opts, ct);
        if (session.Sid is not null)
            await AddToIndexAsync(SidKey(session.TenantKey, session.Sid), session.SessionId, opts, ct);
    }

    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        await cache.RemoveAsync(SessKey(sessionId), ct);
        if (session is null) return;

        var opts = new DistributedCacheEntryOptions { AbsoluteExpiration = session.ExpiresAt };
        await RemoveFromIndexAsync(SubKey(session.TenantKey, session.Subject), sessionId, opts, ct);
        if (session.Sid is not null)
            await RemoveFromIndexAsync(SidKey(session.TenantKey, session.Sid), sessionId, opts, ct);
    }

    public async Task<int> RemoveBySidAsync(string sid, string? tenantKey = null, CancellationToken ct = default)
    {
        await MarkKilledAsync(SidKillKey(tenantKey, sid), ct);
        return await PurgeIndexAsync(SidKey(tenantKey, sid), ct);
    }

    public async Task<int> RemoveBySubjectAsync(string subject, string? tenantKey = null, CancellationToken ct = default)
    {
        // The marker goes down FIRST: a session the roster forgot is dead from this instant, and a
        // session established concurrently with the logout is covered too, because it is stamped
        // after the marker only if it genuinely started after it.
        await MarkKilledAsync(SubKillKey(tenantKey, subject), ct);
        return await PurgeIndexAsync(SubKey(tenantKey, subject), ct);
    }

    private Task MarkKilledAsync(string killKey, CancellationToken ct) =>
        cache.SetStringAsync(
            killKey,
            DateTimeOffset.UtcNow.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = KillMarkerRetention },
            ct);

    // Delete every session id listed under an index key, then the index itself.
    private async Task<int> PurgeIndexAsync(string indexKey, CancellationToken ct)
    {
        var ids = await LoadIndexAsync(indexKey, ct);
        foreach (var id in ids)
            await RemoveAsync(id, ct); // also unlinks the session's other index (sid vs sub)
        await cache.RemoveAsync(indexKey, ct);
        return ids.Count;
    }

    /// <summary>
    /// How many times an index write will re-read and merge before giving up.
    /// </summary>
    /// <remarks>
    /// The index is a JSON blob in IDistributedCache, which offers no compare-and-swap and no set
    /// operations, so a plain load-mutate-store loses one entry whenever two writers interleave.
    /// SetAsync runs on every login AND every token refresh, so writes to a subject's index are
    /// frequent and the interleaving is ordinary rather than exotic.
    ///
    /// The lost entry fails OPEN in the security-relevant direction: a session missing from the index
    /// is invisible to back-channel logout and to "sign out everywhere", and survives with live
    /// access and refresh tokens for the whole session lifetime. Nothing detected or repaired it,
    /// because every repair path consults the same index.
    ///
    /// Verifying the write and merging on a miss converges: if A and B interleave and B wins, A's
    /// verification sees B's blob without A's id, re-reads it, and writes the union. What remains is
    /// a write landing after the final verification — bounded, and reported rather than silent. A
    /// deployment that wants a hard guarantee should back IBffSessionStore with Redis set operations
    /// (SADD/SREM/SMEMBERS), which are atomic; this type exists to work on any IDistributedCache.
    /// </remarks>
    private const int IndexWriteAttempts = 4;

    private async Task AddToIndexAsync(string indexKey, string sessionId, DistributedCacheEntryOptions opts, CancellationToken ct)
    {
        for (var attempt = 0; attempt < IndexWriteAttempts; attempt++)
        {
            var ids = await LoadIndexAsync(indexKey, ct);
            if (ids.Contains(sessionId)) return;

            ids.Add(sessionId);
            await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(ids, BffJsonContext.Default.ListString), opts, ct);

            // Read back rather than assume. A concurrent writer that loaded before this write and
            // stored after it has silently dropped this id; re-reading is what notices.
            var confirmed = await LoadIndexAsync(indexKey, ct);
            if (confirmed.Contains(sessionId)) return;
        }

        // Best effort by design. Under heavy contention an entry can still be dropped here, and that
        // is survivable now: the kill marker terminates the session regardless of what the roster
        // remembers. All that is lost is eager cleanup and an accurate count in the logout log —
        // neither worth failing a login over.
    }

    private async Task RemoveFromIndexAsync(string indexKey, string sessionId, DistributedCacheEntryOptions opts, CancellationToken ct)
    {
        var ids = await LoadIndexAsync(indexKey, ct);
        if (!ids.Remove(sessionId)) return;
        if (ids.Count == 0)
        {
            await cache.RemoveAsync(indexKey, ct);
            return;
        }

        // With the SAME expiry the add path uses. This called the three-argument overload, which
        // supplies default options — no expiration at all — so the first removal from an index made
        // that key immortal while every session it names expired. Redis then accumulated one
        // permanent key per subject and per sid, forever.
        await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(ids, BffJsonContext.Default.ListString), opts, ct);
    }

    private async Task<List<string>> LoadIndexAsync(string indexKey, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(indexKey, ct);
        return json is null
            ? new List<string>()
            : JsonSerializer.Deserialize(json, BffJsonContext.Default.ListString) ?? new List<string>();
    }
}
