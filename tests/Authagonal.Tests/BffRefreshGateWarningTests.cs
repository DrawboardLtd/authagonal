using Authagonal.Bff;
using Authagonal.Core.Clustering;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Authagonal.Tests;

/// <summary>
/// The startup diagnostic for a multi-instance BFF with no cross-replica refresh lock.
/// </summary>
/// <remarks>
/// <see cref="BffRefreshSingleFlightTests"/> proves the race is real and that either lock closes it.
/// This proves an operator is TOLD when they have neither — which is the half that was missing, because
/// the hazard was documented only in XML remarks on <c>AddAuthagonalBff</c> and the coordinator. The
/// deployment that hits it is the one following the README's multi-instance advice, and the README's
/// reader and the deployment's operator are not reliably the same person.
/// <para>
/// Built over a real <c>ServiceProvider</c> rather than a stub, because half of what is under test is the
/// DI shape: what is registered, and that resolving it from a scope does not throw.
/// </para>
/// </remarks>
public sealed class BffRefreshGateWarningTests
{
    [Fact]
    public async Task ASharedCacheWithNoLock_IsReported()
    {
        var (warning, log) = Build(services =>
        {
            services.AddSingleton<IDistributedCache, SharedCacheStandIn>();
        });

        await warning.StartAsync(CancellationToken.None);

        var message = Assert.Single(log.Warnings);
        Assert.Contains("no cross-replica refresh lock", message, StringComparison.Ordinal);
        // Both remedies named, so the message is actionable without going to find the docs.
        Assert.Contains("ILeaseProvider", message, StringComparison.Ordinal);
        Assert.Contains("IBffRefreshLockStore", message, StringComparison.Ordinal);
        // And the third, with the trap that its shipped default is the strict one.
        Assert.Contains("RefreshTokenReuseGraceSeconds", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSingleProcessDefault_IsNotNagged()
    {
        // AddAuthagonalBff installs an in-memory IDistributedCache. A host that never replaced it is
        // running one process, which is the configuration this warning must stay quiet about — otherwise
        // every developer learns to ignore it and it stops working on the deployment that needs it.
        var (warning, log) = Build(_ => { });

        await warning.StartAsync(CancellationToken.None);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task ALeaseProvider_SatisfiesIt()
    {
        var (warning, log) = Build(services =>
        {
            services.AddSingleton<IDistributedCache, SharedCacheStandIn>();
            services.AddSingleton<ILeaseProvider, StubLeaseProvider>();
        });

        await warning.StartAsync(CancellationToken.None);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task AStoreThatImplementsTheLock_SatisfiesIt()
    {
        var (warning, log) = Build(services =>
        {
            services.AddSingleton<IDistributedCache, SharedCacheStandIn>();
            services.AddSingleton<IBffSessionStore, LockingStore>();
        });

        await warning.StartAsync(CancellationToken.None);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task ACustomStoreWithNoLock_IsReportedEvenOnTheInMemoryCache()
    {
        // A custom IBffSessionStore is its own multi-instance signal: the in-memory default already
        // serves a single process, so nobody writes one to run one. The cache it does not use says
        // nothing about it.
        var (warning, log) = Build(services =>
        {
            services.AddSingleton<IBffSessionStore, PlainCustomStore>();
        });

        await warning.StartAsync(CancellationToken.None);

        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task AScopedSessionStore_DoesNotTakeTheHostDownAtStartup()
    {
        // The LegacySecretHashWarning failure mode, kept from recurring: a hosted service is constructed
        // from the ROOT provider, so a store registered per scope cannot be a constructor dependency —
        // it throws before StartAsync is entered and the process never comes up. Resolving inside a
        // scope is what makes a diagnostic cost a diagnostic.
        var (warning, log) = Build(services =>
        {
            services.AddSingleton<IDistributedCache, SharedCacheStandIn>();
            services.AddScoped<IBffSessionStore, PlainCustomStore>();
        });

        await warning.StartAsync(CancellationToken.None);

        Assert.Single(log.Warnings);
    }

    // -----------------------------------------------------------------------

    private static (BffRefreshGateWarning Warning, ListLogger Log) Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // What AddAuthagonalBff installs by default, in the same TryAdd order, so the "untouched host"
        // case under test is the real one.
        services.AddDistributedMemoryCache();
        configure(services);
        // TryAdd last, exactly as AddAuthagonalBff does it, so a store registered above wins.
        services.TryAddSingleton<IBffSessionStore, DistributedCacheBffSessionStore>();

        var provider = services.BuildServiceProvider();
        var log = new ListLogger();
        return (new BffRefreshGateWarning(provider, log), log);
    }

    private sealed class SharedCacheStandIn : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class StubLeaseProvider : ILeaseProvider
    {
        public Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class PlainCustomStore : IBffSessionStore
    {
        public Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default) => Task.FromResult<BffSession?>(null);
        public Task SetAsync(BffSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> RemoveBySidAsync(string sid, string? tenantKey = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> RemoveBySubjectAsync(string subject, string? tenantKey = null, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class LockingStore : PlainCustomStore, IBffRefreshLockStore
    {
        public Task<bool> TryAcquireRefreshLockAsync(string sessionId, TimeSpan ttl, CancellationToken ct = default) => Task.FromResult(true);
        public Task ReleaseRefreshLockAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ListLogger : ILogger<BffRefreshGateWarning>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
