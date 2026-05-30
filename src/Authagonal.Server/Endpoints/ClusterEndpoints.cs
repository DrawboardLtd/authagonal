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
        HttpContext httpContext,
        ClusterNode clusterNode,
        DistributedRateLimiter rateLimiter,
        IOptions<ClusterOptions> options)
    {
        // Reject external callers: require the shared secret when configured, otherwise only
        // accept internal (pod-to-pod) source addresses. Without this an attacker who can reach
        // the endpoint could inflate rate-limit counters to lock out arbitrary IPs/clients.
        if (!InternalEndpointGuard.IsAuthorized(httpContext, options.Value.Secret))
            return Results.NotFound();

        // Detect self-gossip
        if (string.Equals(message.NodeId, clusterNode.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new GossipResponse { NodeId = clusterNode.NodeId, Self = true });
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
