using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Says at startup what state the <c>/_internal/*</c> endpoints are in, because both states are surprising.
/// </summary>
/// <remarks>
/// These endpoints revoke every grant for an arbitrary subject, and they used to authorise on a loopback source
/// address whenever <c>Cluster:Secret</c> was unset — the shipped default. Loopback is what a same-host reverse
/// proxy presents, and the installation docs REQUIRE a TLS-terminating proxy in front of Authagonal, so on a
/// standard single-host deployment the guard saw 127.0.0.1 for every request the proxy forwarded, including
/// internet-originated ones. That made unauthenticated mass session revocation reachable from outside.
/// <para>
/// Now it fails closed, which is right and also invisible: an operator who was relying on the loopback path
/// would find these endpoints answering 404 with nothing to explain it. So both branches are logged — closed,
/// or open by explicit opt-in. Nothing in the product calls them, so the closed state breaks no shipped flow.
/// </para>
/// </remarks>
internal sealed class InternalEndpointAccessWarning(
    IOptions<ClusterOptions> options,
    ILogger<InternalEndpointAccessWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        var cluster = options.Value;

        if (!string.IsNullOrEmpty(cluster.Secret))
        {
            if (cluster.AllowLoopbackWithoutSecret)
            {
                logger.LogInformation(
                    "Cluster:AllowLoopbackWithoutSecret is set but Cluster:Secret is configured, so the " +
                    "secret is what authorises /_internal/* and the loopback opt-in has no effect.");
            }
            return Task.CompletedTask;
        }

        if (cluster.AllowLoopbackWithoutSecret)
        {
            logger.LogWarning(
                "Cluster:Secret is not configured and Cluster:AllowLoopbackWithoutSecret is ON, so " +
                "/_internal/* is authorised by a LOOPBACK SOURCE ADDRESS alone. A reverse proxy on the same " +
                "host connects to Kestrel from 127.0.0.1, so any request it forwards — including one from the " +
                "internet — satisfies that check, and these endpoints revoke every grant for any subject. " +
                "Use this only where nothing proxies to this process. Configure Cluster:Secret instead.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Cluster:Secret is not configured, so the /_internal/* endpoints (pod-to-pod back-channel logout) " +
            "answer 404 to every caller. Configure Cluster:Secret to enable them. This is the safe default: a " +
            "source address is not a credential, and the previous loopback fallback was satisfied by any " +
            "same-host reverse proxy.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
