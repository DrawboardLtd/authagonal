using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Forces the configured <see cref="IRateLimiter"/> to be constructed at host start.
/// </summary>
/// <remarks>
/// Registered only when durable rate limiting is turned on. The limiter is a singleton resolved lazily,
/// so a deployment that asked for cluster-wide limiting without a counter store would boot cleanly and
/// throw on the first request that reached a throttle — which is a login, a token exchange or a SCIM sync
/// failing in production, attributed to whatever endpoint happened to be first. Taking the dependency
/// here turns that into a startup failure with the configuration named.
/// </remarks>
internal sealed class RateLimiterStartupCheck(IRateLimiter limiter) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolution is the check: the factory throws when durable limiting has no store behind it.
        _ = limiter;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
