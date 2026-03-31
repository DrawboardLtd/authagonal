namespace Authagonal.Core.Services;

public interface IRateLimiter
{
    Task<bool> IsRateLimitedAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct = default);
}
