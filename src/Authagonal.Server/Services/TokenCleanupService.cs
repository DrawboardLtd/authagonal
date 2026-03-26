using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class TokenCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<TokenCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

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
