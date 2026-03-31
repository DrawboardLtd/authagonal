namespace Authagonal.Server.Services;

/// <summary>
/// Configuration for in-memory cache durations.
/// Bound from the "Cache" configuration section.
/// </summary>
public sealed class CacheOptions
{
    public int CorsCacheMinutes { get; set; } = 60;
    public int OidcDiscoveryCacheMinutes { get; set; } = 60;
    public int SamlMetadataCacheMinutes { get; set; } = 60;
    public int OidcStateLifetimeMinutes { get; set; } = 10;
    public int SamlReplayLifetimeMinutes { get; set; } = 10;
    public int HealthCheckTimeoutSeconds { get; set; } = 5;
}
