using Authagonal.Core.Stores;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class TokenCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundServiceOptions> bgOptions,
    ILogger<TokenCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(bgOptions.Value.TokenCleanupDelayMinutes), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(bgOptions.Value.TokenCleanupIntervalMinutes));

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var grantStore = scope.ServiceProvider.GetRequiredService<IGrantStore>();

                await grantStore.RemoveExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);

                logger.LogInformation("Token cleanup completed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during token cleanup");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
