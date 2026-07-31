using Authagonal.Core.Stores;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Liveness probe for the storage backend: reads the active signing key, which exercises the same
/// path every token mint depends on.
/// </summary>
/// <remarks>
/// The answer is cached for <see cref="CacheOptions.HealthCheckCacheSeconds"/>. <c>/health</c> is
/// anonymous and deliberately unthrottled — an orchestrator's probes must never be refused — so
/// without a cache every unauthenticated request bought a live store query AND an
/// <see cref="Core.Services.IFieldCipher"/> unwrap of the private signing key, on every replica: a
/// one-line request amplified into database load, repeatable as fast as the caller can send it. The
/// <c>Cache-Control</c> header on the endpoint does not bound this — it is advice to intermediaries,
/// and a caller that ignores it (curl in a loop) reaches the check every time. Liveness does not
/// change faster than the window, so the cached answer is as true as a fresh one.
/// </remarks>
public sealed class TableStorageHealthCheck(ISigningKeyStore signingKeyStore, IOptions<CacheOptions> cacheOptions) : IHealthCheck
{
    private readonly object _gate = new();
    private HealthCheckResult _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var window = TimeSpan.FromSeconds(cacheOptions.Value.HealthCheckCacheSeconds);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (window > TimeSpan.Zero && now - _cachedAt < window)
                return _cached;
        }

        var result = await ProbeAsync(cancellationToken);

        lock (_gate)
        {
            _cached = result;
            _cachedAt = now;
        }

        return result;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(cacheOptions.Value.HealthCheckTimeoutSeconds));

            await signingKeyStore.GetActiveKeyAsync(cts.Token);

            return HealthCheckResult.Healthy("Table Storage is accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Table Storage is not accessible", ex);
        }
    }
}
