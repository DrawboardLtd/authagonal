namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Options for cluster leadership and the cross-node event bus. Bound from the "Cluster" section.
/// </summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// Master switch. When false the node runs standalone (always leader, in-process event bus) —
    /// the right setting for single-node and local development.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shared secret guarding internal pod-to-pod endpoints (e.g. back-channel logout). When unset,
    /// those endpoints fall back to accepting only internal/loopback source addresses.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Leadership lease duration in seconds. Renewed at roughly half this interval.</summary>
    public int LeaseTtlSeconds { get; set; } = 30;

    /// <summary>How often (seconds) the event-bus backend polls for messages published by other nodes.</summary>
    public int PollIntervalSeconds { get; set; } = 3;
}
