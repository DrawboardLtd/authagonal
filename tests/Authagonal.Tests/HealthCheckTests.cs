using System.Net;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

public sealed class HealthCheckTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // F200 — /health is anonymous and unthrottled, so it must not be a free
    // storage query (plus a private-key unwrap) per request.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RepeatedProbes_HitStorageOncePerCacheWindow()
    {
        var store = new CountingSigningKeyStore();
        var check = new TableStorageHealthCheck(store, Options.Create(new CacheOptions()));

        for (var i = 0; i < 20; i++)
            Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(Context())).Status);

        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task RepeatedProbes_WithCachingDisabled_HitStorageEveryTime()
    {
        // The control for the test above: with the window at zero every probe reaches the store, which
        // is exactly the amplification an unauthenticated caller could drive before the cache existed.
        var store = new CountingSigningKeyStore();
        var check = new TableStorageHealthCheck(
            store, Options.Create(new CacheOptions { HealthCheckCacheSeconds = 0 }));

        for (var i = 0; i < 5; i++)
            await check.CheckHealthAsync(Context());

        Assert.Equal(5, store.Reads);
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("table_storage", _ => null!, null, null),
    };

    private sealed class CountingSigningKeyStore : ISigningKeyStore
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult<SigningKeyInfo?>(null);
        }

        public Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SigningKeyInfo>>([]);

        public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateKeyAsync(string keyId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string keyId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
