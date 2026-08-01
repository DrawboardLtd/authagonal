using System.Collections.Concurrent;
using Authagonal.Bff;
using Authagonal.Core.Clustering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// F89 — refresh single-flight across BFF replicas.
/// </summary>
/// <remarks>
/// Two coordinators over one shared session store stand in for two replicas: that is exactly the
/// deployment AddAuthagonalBff documents (a shared IDistributedCache holding the session and its
/// refresh token), and the per-process semaphore inside a coordinator does nothing across them. When
/// both redeem the same rotating refresh token the IdP reads the second redemption as a stolen-token
/// replay and revokes the whole grant family — the user is signed out everywhere by ordinary
/// concurrent load. <see cref="ReplayingTokenClient"/> models that: a second redemption of an
/// already-rotated handle fails, and the losing request is treated as logged out.
/// </remarks>
public sealed class BffRefreshSingleFlightTests
{
    [Fact]
    public async Task TwoReplicas_WithALeaseProvider_RedeemTheRefreshTokenOnce()
    {
        var store = new SharedSessionStore();
        var tokens = new ReplayingTokenClient();
        var leases = new ExclusiveLeaseProvider();

        var replicaA = Coordinator(store, tokens, leases);
        var replicaB = Coordinator(store, tokens, leases);

        var session = await SeedNearExpirySessionAsync(store);

        // A takes the lease and parks inside the token call; B arrives while it is held.
        var first = replicaA.EnsureFreshAsync(session);
        await tokens.FirstEntered.Task;
        var second = replicaB.EnsureFreshAsync(session);

        tokens.ReleaseRefresh();

        var a = await first;
        var b = await second;

        Assert.Equal(1, tokens.Redemptions);
        // Neither request was signed out, and both are serving the tokens the single redemption produced.
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("access-2", a!.AccessToken);
        Assert.Equal("access-2", b!.AccessToken);
        Assert.NotNull(await store.GetAsync(session.SessionId));
    }

    [Fact]
    public async Task TwoReplicas_WithNoLeaseProvider_BothRedeem_AndTheLoserIsSignedOut()
    {
        // The guard's non-vacuity, and the reported failure verbatim: with no cross-process lock the
        // in-process semaphore holds nothing between replicas, so the second redemption happens and the
        // IdP's replay response takes the session with it.
        var store = new SharedSessionStore();
        var tokens = new ReplayingTokenClient();

        var replicaA = Coordinator(store, tokens, leases: null);
        var replicaB = Coordinator(store, tokens, leases: null);

        var session = await SeedNearExpirySessionAsync(store);

        var first = replicaA.EnsureFreshAsync(session);
        await tokens.FirstEntered.Task;
        var second = replicaB.EnsureFreshAsync(session);

        // Both are inside the token call with the SAME handle before either completes — nothing stopped
        // the second one from getting there.
        await tokens.SecondEntered.Task;
        tokens.ReleaseRefresh();

        var a = await first;
        var b = await second;

        Assert.Equal(2, tokens.Redemptions);
        // One of them presented an already-rotated token: that request is signed out, and in production
        // the IdP has revoked the grant family the other one is still holding.
        Assert.True(a is null || b is null);
    }

    // -----------------------------------------------------------------------

    private static BffRefreshCoordinator Coordinator(
        SharedSessionStore store, ReplayingTokenClient tokens, ILeaseProvider? leases)
    {
        var options = Options.Create(new AuthagonalBffOptions
        {
            Authority = "https://idp.example",
            ClientId = "bff",
            ClientSecret = "secret",
            RefreshThresholdSeconds = 60,
        });

        return new BffRefreshCoordinator(
            tokens, store, new SingleTenantResolver(), options,
            NullLogger<BffRefreshCoordinator>.Instance, leases);
    }

    private static async Task<BffSession> SeedNearExpirySessionAsync(SharedSessionStore store)
    {
        var session = new BffSession
        {
            SessionId = "session-1",
            Subject = "user-1",
            IdToken = "id-1",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            // Inside the 60s refresh threshold but not yet expired: the state every replica sees at once.
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SetAsync(session);
        return session;
    }

    /// <summary>Stands in for the shared IDistributedCache every replica reads the session from.</summary>
    private sealed class SharedSessionStore : IBffSessionStore
    {
        private readonly ConcurrentDictionary<string, BffSession> _sessions = new();

        public Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? Clone(s) : null);

        public Task SetAsync(BffSession session, CancellationToken ct = default)
        {
            _sessions[session.SessionId] = Clone(session);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken ct = default)
        {
            _sessions.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        public Task<int> RemoveBySidAsync(string sid, string? tenantKey = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RemoveBySubjectAsync(string subject, string? tenantKey = null, CancellationToken ct = default)
            => Task.FromResult(0);

        // A real store round-trips through serialization, so each replica mutates its own copy.
        private static BffSession Clone(BffSession s) => new()
        {
            SessionId = s.SessionId,
            TenantKey = s.TenantKey,
            Sid = s.Sid,
            Subject = s.Subject,
            IdToken = s.IdToken,
            AccessToken = s.AccessToken,
            RefreshToken = s.RefreshToken,
            AccessTokenExpiresAt = s.AccessTokenExpiresAt,
            ExpiresAt = s.ExpiresAt,
            CreatedAt = s.CreatedAt,
            Claims = new Dictionary<string, string>(s.Claims),
        };
    }

    /// <summary>
    /// An IdP that rotates: the first redemption of a handle succeeds, a second is replay and fails —
    /// which in production also revokes the family behind it.
    /// </summary>
    private sealed class ReplayingTokenClient : ITokenClient
    {
        private readonly ConcurrentDictionary<string, byte> _spent = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _redemptions;
        private int _entered;
        private int _issued = 1;

        /// <summary>Signalled as each caller reaches the token call, so a test can hold them together.</summary>
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Redemptions => Volatile.Read(ref _redemptions);

        public void ReleaseRefresh() => _gate.TrySetResult();

        public async Task<TokenResult> RefreshAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _entered) == 1)
                FirstEntered.TrySetResult();
            else
                SecondEntered.TrySetResult();

            await _gate.Task;

            Interlocked.Increment(ref _redemptions);
            if (!_spent.TryAdd(refreshToken, 0))
                throw new BffTokenException("invalid_grant (refresh token replay — grant family revoked)");

            var n = Interlocked.Increment(ref _issued);
            return new TokenResult($"access-{n}", $"refresh-{n}", $"id-{n}", 3600);
        }

        public Task<TokenResult> ExchangeCodeAsync(BffTenantConfig tenant, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RevokeAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<TokenResult> ExchangeTokenAsync(BffTenantConfig tenant, string subjectToken, IReadOnlyDictionary<string, string>? extraParameters = null, string? scope = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A lease with real mutual exclusion — what a storage-backed ILeaseProvider gives across replicas,
    /// unlike InProcessLeaseProvider, which always grants because it only ever sees one node.
    /// </summary>
    private sealed class ExclusiveLeaseProvider : ILeaseProvider
    {
        private readonly ConcurrentDictionary<string, string> _holders = new();

        public Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
            => Task.FromResult(_holders.GetOrAdd(resource, nodeId) == nodeId);

        public Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
        {
            _holders.TryRemove(new KeyValuePair<string, string>(resource, nodeId));
            return Task.CompletedTask;
        }
    }

    private sealed class SingleTenantResolver : IBffTenantResolver
    {
        private static readonly BffTenantConfig Tenant = new()
        {
            Authority = "https://idp.example",
            ClientId = "bff",
            ClientSecret = "secret",
            Scope = ["openid", "offline_access"],
        };

        public Task<BffTenantConfig?> ResolveAsync(string? tenantKey, CancellationToken ct = default)
            => Task.FromResult<BffTenantConfig?>(Tenant);

        public Task<BffTenantConfig?> ResolveByIssuerAsync(string issuer, CancellationToken ct = default)
            => Task.FromResult<BffTenantConfig?>(Tenant);
    }
}
