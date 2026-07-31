using System.Text.Json;
using Authagonal.Bff;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// F84 — the BFF's subject/sid indexes are the only route a back-channel logout has to a session, and
/// they were maintained by an unguarded read-modify-write over a JSON blob.
/// </summary>
public sealed class BffSessionIndexTests
{
    // internal type, reached through InternalsVisibleTo.
    private static IBffSessionStore NewStore(IDistributedCache cache) =>
        new DistributedCacheBffSessionStore(cache);

    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>
    /// A cache whose reads yield, so concurrent read-modify-write cycles actually overlap.
    /// </summary>
    /// <remarks>
    /// MemoryDistributedCache completes synchronously, so awaiting it never suspends and 25 "parallel"
    /// SetAsync calls run one after another — a test over it would pass against the unguarded
    /// implementation and prove nothing. Yielding on read puts every writer's load before any writer's
    /// store, which is the worst case the real defect occurs in.
    /// </remarks>
    private sealed class InterleavingCache(IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            await Task.Yield();
            return await inner.GetAsync(key, token);
        }

        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            await Task.Yield();
            await inner.SetAsync(key, value, options, token);
        }
    }

    private static BffSession Session(string id, string subject = "user-1", string? sid = null) => new()
    {
        SessionId = id,
        Subject = subject,
        Sid = sid,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
    };

    [Fact]
    public async Task InterleavedWrites_ForOneSubject_AreAllKillable()
    {
        // SetAsync runs on every login AND every token refresh, so concurrent writes to one subject's
        // index are ordinary. A lost entry fails OPEN: the session is invisible to back-channel logout
        // and to "sign out everywhere", and survives with live access and refresh tokens for the whole
        // session lifetime, with nothing to detect or repair it.
        var store = NewStore(new InterleavingCache(NewCache()));
        const int count = 25;

        await Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => Task.Run(() => store.SetAsync(Session($"s{i}")))));

        await store.RemoveBySubjectAsync("user-1");

        // Every session is dead, not just the ones the roster happened to remember. The returned
        // count is deliberately NOT asserted — it comes from the roster, is only logged, and under
        // this much contention the roster is genuinely incomplete. Making the count exact would mean
        // making the roster exact, which IDistributedCache cannot do; making the KILL exact is what
        // matters, and does not depend on the roster at all.
        for (var i = 0; i < count; i++)
            Assert.Null(await store.GetAsync($"s{i}"));
    }

    [Fact]
    public async Task InterleavedWrites_ForOneSid_AreAllKillable()
    {
        var store = NewStore(new InterleavingCache(NewCache()));
        const int count = 25;

        await Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => Task.Run(() => store.SetAsync(Session($"t{i}", sid: "sid-1")))));

        await store.RemoveBySidAsync("sid-1");

        for (var i = 0; i < count; i++)
            Assert.Null(await store.GetAsync($"t{i}"));
    }

    [Fact]
    public async Task ARefreshAfterLogout_CannotCarryTheSessionPastIt()
    {
        // SetAsync runs on every token refresh. If it re-stamped the session's establishment time,
        // a refresh in flight across a logout would push the session past the revocation marker and
        // undo it — so the stamp is written once and only once.
        var store = NewStore(NewCache());
        var session = Session("live");
        await store.SetAsync(session);

        await store.RemoveBySubjectAsync("user-1");

        session.AccessToken = "refreshed";
        await store.SetAsync(session);

        Assert.Null(await store.GetAsync("live"));
    }

    [Fact]
    public async Task ALoginAfterLogout_IsNotRevoked()
    {
        // The marker must not become a permanent ban on the subject.
        var store = NewStore(NewCache());
        await store.SetAsync(Session("old"));
        await store.RemoveBySubjectAsync("user-1");

        await Task.Delay(2);
        await store.SetAsync(Session("new"));

        Assert.NotNull(await store.GetAsync("new"));
        Assert.Null(await store.GetAsync("old"));
    }

    [Fact]
    public async Task RemovingOneSession_KeepsTheIndexExpiring()
    {
        // The remove path wrote the shrunken index back through the three-argument overload, which
        // supplies default options — no expiration. The first removal therefore made the index key
        // permanent while every session it named expired, so Redis accumulated one immortal key per
        // subject and per sid, forever.
        var cache = new ExpiryRecordingCache(NewCache());
        var store = NewStore(cache);

        await store.SetAsync(Session("a"));
        await store.SetAsync(Session("b"));
        await store.RemoveAsync("a");

        var indexWrites = cache.Writes.Where(w => w.Key.StartsWith("agbff:sub:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(indexWrites);
        Assert.All(indexWrites, w => Assert.NotNull(w.AbsoluteExpiration));
    }

    /// <summary>Records the options every write was made with, which is the thing being asserted.</summary>
    private sealed class ExpiryRecordingCache(IDistributedCache inner) : IDistributedCache
    {
        public List<(string Key, DateTimeOffset? AbsoluteExpiration)> Writes { get; } = [];

        public byte[]? Get(string key) => inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Writes.Add((key, options.AbsoluteExpiration));
            inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Writes.Add((key, options.AbsoluteExpiration));
            return inner.SetAsync(key, value, options, token);
        }
    }
}
