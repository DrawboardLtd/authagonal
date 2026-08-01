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

    /// <summary>
    /// How long the /health probe's answer is reused before the storage backend is queried again.
    /// Matches the <c>Cache-Control: max-age</c> the endpoint advertises. Set to 0 to probe on every
    /// request — which re-opens the anonymous amplification the cache exists to close.
    /// </summary>
    public int HealthCheckCacheSeconds { get; set; } = 5;
}
