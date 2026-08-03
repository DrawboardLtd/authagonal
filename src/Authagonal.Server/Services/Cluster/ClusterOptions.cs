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
    /// Shared secret guarding internal pod-to-pod endpoints (e.g. back-channel logout). Required: without
    /// it those endpoints answer 404 to everyone.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Development-only escape hatch: authorise <c>/_internal/*</c> on a loopback source address when no
    /// <see cref="Secret"/> is configured. Off by default, and logs a warning at startup when enabled.
    /// </summary>
    /// <remarks>
    /// Loopback was the fallback, and loopback is exactly what this project's own MANDATORY deployment shape
    /// produces. The installation and configuration docs require Authagonal to sit behind a TLS-terminating
    /// reverse proxy, and a proxy on the same host — nginx or Caddy with <c>proxy_pass
    /// http://127.0.0.1:8080</c>, IIS/ANCM, <c>network_mode: host</c> — connects to Kestrel *from* 127.0.0.1.
    /// The raw-peer capture faithfully records that real peer, so the guard saw 127.0.0.1 for every request the
    /// proxy forwarded, including ones that originated on the internet.
    /// <para>
    /// The route is anonymous, antiforgery-disabled and revokes every grant for an arbitrary subject, so the
    /// consequence was unauthenticated mass session revocation for any user, and a probe for whether a given
    /// subject had sessions at all. The earlier fix correctly stopped trusting forwarded headers and correctly
    /// stopped accepting RFC1918 on the reasoning that "a source address is not a credential" — and then kept
    /// one source address as a credential.
    /// </para>
    /// <para>
    /// Nothing in the product calls these endpoints, so failing closed breaks no shipped flow: pod-to-pod
    /// callers are on different addresses and already needed the secret.
    /// </para>
    /// </remarks>
    public bool AllowLoopbackWithoutSecret { get; set; }

    /// <summary>Leadership lease duration in seconds. Renewed at roughly half this interval.</summary>
    public int LeaseTtlSeconds { get; set; } = 30;

    /// <summary>
    /// Whether this node runs the lease-renewal loop and can therefore become leader. Default true.
    /// </summary>
    /// <remarks>
    /// The equivalent existed only as a parameter on <c>AddAuthagonalClustering</c>, and
    /// <c>AddAuthagonal</c> — the way every documented deployment wires this up — called it without
    /// exposing the parameter. So a node that "must receive cluster events but must never hold leadership"
    /// could not actually be excluded through configuration. Distinct from <see cref="Enabled"/>: false here
    /// still joins the cluster and consumes the event bus, it just never contends for the lease.
    /// </remarks>
    public bool RunLeaderElection { get; set; } = true;

    /// <summary>How often (seconds) the event-bus backend polls for messages published by other nodes.</summary>
    public int PollIntervalSeconds { get; set; } = 3;
}
