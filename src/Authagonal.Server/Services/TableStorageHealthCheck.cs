using Authagonal.Core.Stores;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Authagonal.Server.Services;

public sealed class TableStorageHealthCheck(ISigningKeyStore signingKeyStore) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            await signingKeyStore.GetActiveKeyAsync(cts.Token);

            return HealthCheckResult.Healthy("Table Storage is accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Table Storage is not accessible", ex);
        }
    }
}
