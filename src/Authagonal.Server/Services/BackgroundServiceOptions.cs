namespace Authagonal.Server.Services;

/// <summary>
/// Configuration for background service intervals.
/// Bound from the "BackgroundServices" configuration section.
/// </summary>
public sealed class BackgroundServiceOptions
{
    public int TokenCleanupDelayMinutes { get; set; } = 5;
    public int TokenCleanupIntervalMinutes { get; set; } = 60;
    public int GrantReconciliationDelayMinutes { get; set; } = 10;
    public int GrantReconciliationIntervalMinutes { get; set; } = 30;
}
