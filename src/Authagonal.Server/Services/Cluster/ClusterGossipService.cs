using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Cluster;

public sealed class ClusterGossipService(
    PeerRegistry peerRegistry,
    ClusterNode clusterNode,
    DistributedRateLimiter rateLimiter,
    IHttpClientFactory httpClientFactory,
    IOptions<ClusterOptions> options,
    ILogger<ClusterGossipService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("Cluster gossip service started, interval={Interval}s, nodeId={NodeId}",
            opts.GossipIntervalSeconds, clusterNode.NodeId);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.GossipIntervalSeconds));

        do
        {
            try
            {
                await GossipRoundAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during gossip round");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task GossipRoundAsync(ClusterOptions opts, CancellationToken ct)
    {
        var localState = rateLimiter.GetLocalState();

        // Gossip to all discovered peers (direct, no LB)
        var peers = peerRegistry.GetPeers();
        foreach (var peer in peers)
        {
            await GossipToPeerAsync(peer.HttpAddress, localState, ct);
        }

        // Also gossip through InternalUrl if configured (LB fallback)
        if (!string.IsNullOrWhiteSpace(opts.InternalUrl))
        {
            await GossipViaLoadBalancerAsync(opts.InternalUrl, opts.Secret, localState, ct);
        }

        // Prune stale peers and expired windows
        rateLimiter.Prune(TimeSpan.FromSeconds(opts.PeerStaleAfterSeconds));
    }

    private async Task GossipToPeerAsync(string peerAddress, GossipMessage localState, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("ClusterGossip");
            var url = $"{peerAddress.TrimEnd('/')}/_internal/cluster/gossip";

            var response = await client.PostAsJsonAsync(url, localState, ct);

            if (response.IsSuccessStatusCode)
            {
                var gossipResponse = await response.Content.ReadFromJsonAsync<GossipResponse>(ct);
                if (gossipResponse is not null && !gossipResponse.Self && gossipResponse.Counters is not null)
                {
                    rateLimiter.MergePeerState(gossipResponse.NodeId, gossipResponse.Counters);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to gossip to peer {PeerAddress}", peerAddress);
        }
    }

    private async Task GossipViaLoadBalancerAsync(string internalUrl, string? secret, GossipMessage localState, CancellationToken ct)
    {
        const int maxRetries = 2;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var client = httpClientFactory.CreateClient("ClusterGossip");
                var url = $"{internalUrl.TrimEnd('/')}/_internal/cluster/gossip";

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(localState)
                };

                if (!string.IsNullOrWhiteSpace(secret))
                    request.Headers.Add("X-Cluster-Secret", secret);

                var response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var gossipResponse = await response.Content.ReadFromJsonAsync<GossipResponse>(ct);
                    if (gossipResponse is null)
                        return;

                    if (gossipResponse.Self)
                    {
                        // Hit ourselves through the LB — retry
                        logger.LogDebug("Self-gossip detected via LB, attempt {Attempt}/{MaxRetries}", attempt + 1, maxRetries + 1);
                        continue;
                    }

                    if (gossipResponse.Counters is not null)
                    {
                        rateLimiter.MergePeerState(gossipResponse.NodeId, gossipResponse.Counters);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to gossip via LB {Url}, attempt {Attempt}", internalUrl, attempt + 1);
            }
        }
    }
}
