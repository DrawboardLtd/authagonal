using Authagonal.Core.Stores;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class TableStorageHealthCheck(ISigningKeyStore signingKeyStore, IOptions<CacheOptions> cacheOptions) : IHealthCheck
{

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
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
