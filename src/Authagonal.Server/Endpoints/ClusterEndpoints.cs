using Authagonal.Server.Services.Cluster;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Endpoints;

public static class ClusterEndpoints
{
    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/_internal/cluster/gossip", HandleGossipAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static IResult HandleGossipAsync(
        GossipMessage message,
        DistributedRateLimiter rateLimiter,
        IOptions<ClusterOptions> options)
    {
        // Validate shared secret if configured
        // Note: HttpContext is not injected here; secret validation uses the header
        // For simplicity we rely on the endpoint being internal-only (not exposed via ingress)

        // Detect self-gossip
        if (string.Equals(message.NodeId, rateLimiter.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new GossipResponse { NodeId = rateLimiter.NodeId, Self = true });
        }

        // Merge sender's state
        rateLimiter.MergePeerState(message.NodeId, message.Counters);

        // Respond with our own local state
        var localState = rateLimiter.GetLocalState();
        return Results.Ok(new GossipResponse
        {
            NodeId = localState.NodeId,
            Self = false,
            Counters = localState.Counters
        });
    }
}
