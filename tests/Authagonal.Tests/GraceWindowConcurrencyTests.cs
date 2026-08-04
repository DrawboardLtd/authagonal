using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// The grace-window retry cannot resurrect a refresh grant that was consumed or revoked underneath it.
/// </summary>
/// <remarks>
/// <c>ReissueFromSuccessorAsync</c> reads the successor grant, guarded on <c>!ConsumedAt.HasValue</c>, appends
/// the access-token jti it just minted, and persisted with <c>StoreAsync</c> — an unconditional full-row
/// upsert on every provider. The instance written carries <c>ConsumedAt = null</c>, and on DynamoDB and SQL the
/// write also DROPS the top-level <c>consumedAt</c> guard attribute that <c>TryMarkConsumedAsync</c> conditions
/// on.
/// <para>
/// So any consume or delete landing between the read and the write was silently undone: a revoked grant came
/// back, and rotation-replay detection stopped seeing the marker it depends on. This is the
/// read-modify-blind-write shape already eliminated for the device-poll timestamp, for
/// <c>RecordSuccessfulLoginAsync</c>, and for the profile-revert compensation.
/// </para>
/// </remarks>
public sealed class GraceWindowConcurrencyTests
{
    private static PersistedGrant Refresh(string key) => new()
    {
        Key = key,
        Type = "refresh_token",
        SubjectId = "user-1",
        ClientId = "client-1",
        Data = """{"scopes":["openid"]}""",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    /// <summary>A consumed grant does not come back to life.</summary>
    [Fact]
    public async Task AConsumedGrantIsNotResurrected()
    {
        var store = new InMemoryGrantStore();
        await store.StoreAsync(Refresh("rt-1"));

        // The legitimate client's next rotation consumes it.
        var consuming = Refresh("rt-1");
        consuming.Data = """{"scopes":["openid"],"successorKey":"rt-2"}""";
        Assert.True(await store.TryMarkConsumedAsync(consuming));

        // The grace-window retry, working from its earlier read, tries to amend the same row.
        var amended = Refresh("rt-1");
        amended.Data = """{"scopes":["openid"],"accessTokens":[{"jti":"at-9"}]}""";

        Assert.False(await store.TryUpdateDataIfUnconsumedAsync(amended));

        // The consumed marker survives — it is what replay detection reads.
        var stored = await store.GetAsync("rt-1");
        Assert.NotNull(stored!.ConsumedAt);
        Assert.Contains("successorKey", stored.Data);
        Assert.DoesNotContain("at-9", stored.Data);
    }

    /// <summary>A revoked (deleted) grant is not recreated.</summary>
    /// <remarks>
    /// The upsert would have re-inserted the row, so a logout or an admin revocation racing a retry left a
    /// live refresh grant behind for a session that had been ended.
    /// </remarks>
    [Fact]
    public async Task ARevokedGrantIsNotRecreated()
    {
        var store = new InMemoryGrantStore();
        await store.StoreAsync(Refresh("rt-3"));

        await store.RemoveAsync("rt-3");

        Assert.False(await store.TryUpdateDataIfUnconsumedAsync(Refresh("rt-3")));
        Assert.Null(await store.GetAsync("rt-3"));
    }

    /// <summary>The control: a live, un-consumed grant is amended.</summary>
    /// <remarks>
    /// Without this, a primitive that refused everything would satisfy both tests above — and the append is
    /// what lets revoking a refresh token reach the access token issued on a retry, which the grace path's own
    /// comment says is the point of the write.
    /// </remarks>
    [Fact]
    public async Task ALiveGrantIsAmended()
    {
        var store = new InMemoryGrantStore();
        await store.StoreAsync(Refresh("rt-4"));

        var amended = Refresh("rt-4");
        amended.Data = """{"scopes":["openid"],"accessTokens":[{"jti":"at-7"}]}""";

        Assert.True(await store.TryUpdateDataIfUnconsumedAsync(amended));

        var stored = await store.GetAsync("rt-4");
        Assert.Contains("at-7", stored!.Data);
        Assert.Null(stored.ConsumedAt);
    }

    /// <summary>
    /// A concurrent consume and a concurrent amend cannot both win.
    /// </summary>
    /// <remarks>
    /// The property the durable stores get from an ETag / condition expression, asserted here against the
    /// double so the contract is stated somewhere a change to any provider has to keep.
    /// </remarks>
    [Fact]
    public async Task AConsumeAndAnAmendCannotBothWin()
    {
        var store = new InMemoryGrantStore();
        await store.StoreAsync(Refresh("rt-5"));

        var consume = Task.Run(() => store.TryMarkConsumedAsync(Refresh("rt-5")));
        var amend = Task.Run(() => store.TryUpdateDataIfUnconsumedAsync(Refresh("rt-5")));

        var results = await Task.WhenAll(consume, amend);

        // The consume always eventually wins or loses cleanly; what must never happen is the amend
        // succeeding AFTER a consume, because that is what cleared the marker.
        var stored = await store.GetAsync("rt-5");
        if (results[0])
            Assert.NotNull(stored!.ConsumedAt);
    }

    /// <summary>
    /// When the conditional write loses, the retry is REFUSED rather than served with an untracked token.
    /// </summary>
    /// <remarks>
    /// The race itself — successor un-consumed at the read, consumed before the write — cannot be produced by
    /// sequential calls, so the store is decorated to lose it deterministically. Serving the retry anyway
    /// would hand out an access token whose jti is recorded on no live grant, and therefore one revocation
    /// cannot reach; falling through to replay handling is right because at that point this presentation of
    /// the old handle really is outside the safe window.
    /// <para>
    /// Driven through <c>IProtocolTokenService</c> rather than over HTTP, as the sibling grace-window tests
    /// are: the grace window has to be turned on (<c>AuthOptions.RefreshTokenReuseGraceSeconds</c> defaults to
    /// 0), and a first version of this test went through /connect/token and passed against the UNFIXED code
    /// because it never entered the grace path at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenTheConditionalWriteLosesTheRetryIsRefused()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.RefreshTokenReuseGraceSeconds = 30,
            GrantStoreDecorator = inner => new LosesTheAmendRace(inner),
        };
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>()
            .GetAsync(AuthagonalTestFactory.TestClientId))!;
        var subject = await resolver.BuildSubjectAsync(user, client);

        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        // First refresh rotates cleanly and leaves a live successor.
        var first = await tokens.HandleRefreshTokenAsync(handle, AuthagonalTestFactory.TestClientId);
        Assert.NotNull(first.RefreshToken);

        // The retry lands in the grace window, and the amend loses the race. It must be refused, and the
        // family revoked — not served with a token nothing can revoke.
        await Assert.ThrowsAnyAsync<Exception>(
            () => tokens.HandleRefreshTokenAsync(handle, AuthagonalTestFactory.TestClientId));
    }

    /// <summary>A grant store whose conditional amend always loses, as a concurrent consumer would make it.</summary>
    private sealed class LosesTheAmendRace(IGrantStore inner) : IGrantStore
    {
        public Task<bool> TryUpdateDataIfUnconsumedAsync(PersistedGrant grant, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task StoreAsync(PersistedGrant grant, CancellationToken ct = default) => inner.StoreAsync(grant, ct);
        public Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public Task ConsumeAsync(string key, CancellationToken ct = default) => inner.ConsumeAsync(key, ct);
        public Task<bool> TryConsumeAsync(string key, CancellationToken ct = default) => inner.TryConsumeAsync(key, ct);
        public Task<bool> TryMarkConsumedAsync(PersistedGrant grant, CancellationToken ct = default)
            => inner.TryMarkConsumedAsync(grant, ct);
        public Task RemoveAsync(string key, CancellationToken ct = default) => inner.RemoveAsync(key, ct);
        public Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default)
            => inner.RemoveAllBySubjectAsync(subjectId, ct);
        public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
            => inner.RemoveAllBySubjectAndClientAsync(subjectId, clientId, ct);
        public Task RemoveBySubjectAsync(string subjectId, IReadOnlyCollection<string> types, string? clientId = null, CancellationToken ct = default)
            => inner.RemoveBySubjectAsync(subjectId, types, clientId, ct);
        public Task<int> RemoveBySessionAsync(string subjectId, IReadOnlyCollection<string> types, string sessionId, bool invert = false, CancellationToken ct = default)
            => inner.RemoveBySessionAsync(subjectId, types, sessionId, invert, ct);
        public Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
            => inner.GetBySubjectAsync(subjectId, ct);
        public Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => inner.RemoveExpiredAsync(cutoff, ct);
    }

    /// <summary>An empty handle is refused, as every other write on this store refuses it.</summary>
    /// <remarks>
    /// Grants read back from storage carry no Key, so re-storing one without setting it lands in the
    /// SHA-256("") partition on the real stores. The double throws to make that impossible to miss, and this
    /// primitive has to throw for the same reason.
    /// </remarks>
    [Fact]
    public async Task AnEmptyHandleIsRefused()
    {
        var store = new InMemoryGrantStore();
        var keyless = Refresh("rt-6");
        keyless.Key = "";

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryUpdateDataIfUnconsumedAsync(keyless));
    }
}
