using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Authagonal.Bff;

/// <summary>Default <see cref="IBffSessionStore"/> over <c>IDistributedCache</c>. Sessions are keyed by
/// their opaque id; secondary indexes map each subject (and each OIDC <c>sid</c>, when present) to the
/// set of session ids sharing it, so a back-channel logout can find and kill them.</summary>
internal sealed class DistributedCacheBffSessionStore(IDistributedCache cache) : IBffSessionStore
{
    private static string SessKey(string id) => $"agbff:sess:{id}";
    private static string SidKey(string sid) => $"agbff:sid:{sid}";
    private static string SubKey(string sub) => $"agbff:sub:{sub}";

    public async Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(SessKey(sessionId), ct);
        return json is null ? null : JsonSerializer.Deserialize(json, BffJsonContext.Default.BffSession);
    }

    public async Task SetAsync(BffSession session, CancellationToken ct = default)
    {
        var opts = new DistributedCacheEntryOptions { AbsoluteExpiration = session.ExpiresAt };
        await cache.SetStringAsync(
            SessKey(session.SessionId),
            JsonSerializer.Serialize(session, BffJsonContext.Default.BffSession),
            opts, ct);

        await AddToIndexAsync(SubKey(session.Subject), session.SessionId, opts, ct);
        if (session.Sid is not null)
            await AddToIndexAsync(SidKey(session.Sid), session.SessionId, opts, ct);
    }

    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        await cache.RemoveAsync(SessKey(sessionId), ct);
        if (session is null) return;

        await RemoveFromIndexAsync(SubKey(session.Subject), sessionId, ct);
        if (session.Sid is not null)
            await RemoveFromIndexAsync(SidKey(session.Sid), sessionId, ct);
    }

    public Task<int> RemoveBySidAsync(string sid, CancellationToken ct = default)
        => PurgeIndexAsync(SidKey(sid), ct);

    public Task<int> RemoveBySubjectAsync(string subject, CancellationToken ct = default)
        => PurgeIndexAsync(SubKey(subject), ct);

    // Delete every session id listed under an index key, then the index itself.
    private async Task<int> PurgeIndexAsync(string indexKey, CancellationToken ct)
    {
        var ids = await LoadIndexAsync(indexKey, ct);
        foreach (var id in ids)
            await RemoveAsync(id, ct); // also unlinks the session's other index (sid vs sub)
        await cache.RemoveAsync(indexKey, ct);
        return ids.Count;
    }

    private async Task AddToIndexAsync(string indexKey, string sessionId, DistributedCacheEntryOptions opts, CancellationToken ct)
    {
        var ids = await LoadIndexAsync(indexKey, ct);
        if (ids.Contains(sessionId)) return;
        ids.Add(sessionId);
        await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(ids, BffJsonContext.Default.ListString), opts, ct);
    }

    private async Task RemoveFromIndexAsync(string indexKey, string sessionId, CancellationToken ct)
    {
        var ids = await LoadIndexAsync(indexKey, ct);
        if (!ids.Remove(sessionId)) return;
        if (ids.Count == 0)
            await cache.RemoveAsync(indexKey, ct);
        else
            await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(ids, BffJsonContext.Default.ListString), ct);
    }

    private async Task<List<string>> LoadIndexAsync(string indexKey, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(indexKey, ct);
        return json is null
            ? new List<string>()
            : JsonSerializer.Deserialize(json, BffJsonContext.Default.ListString) ?? new List<string>();
    }
}
