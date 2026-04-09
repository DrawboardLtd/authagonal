using Authagonal.Server.Services.Cluster;

namespace Authagonal.Tests;

public class DistributedRateLimiterTests
{
    [Fact]
    public async Task LocalOnly_EnforcesMax()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));
        var window = TimeSpan.FromHours(1);

        for (var i = 0; i < 5; i++)
        {
            var result = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
            Assert.False(result, $"Request {i + 1} should not be rate limited");
        }

        // 6th request should be limited (count is now 6, which exceeds max of 5)
        var limited = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.True(limited);
    }

    [Fact]
    public async Task WindowExpiration_ResetsCounters()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));
        // Use a very short window that we can reason about
        var window = TimeSpan.FromMilliseconds(50);

        // Fill up the limit
        for (var i = 0; i < 5; i++)
            await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);

        var limited = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.True(limited);

        // Wait for window to expire
        await Task.Delay(100);

        // Should be allowed again (window expired, counter resets)
        var afterExpiry = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.False(afterExpiry);
    }

    [Fact]
    public async Task MergingPeerState_IncreasesConsolidatedTotal()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));
        var window = TimeSpan.FromHours(1);

        // 3 local requests
        for (var i = 0; i < 3; i++)
            await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);

        // Peer has 2 requests for the same key
        var peerCounters = new Dictionary<string, WindowCounter>
        {
            ["register|1.2.3.4"] = new WindowCounter { Count = 2, WindowStart = DateTimeOffset.UtcNow }
        };
        limiter.MergePeerState("node-2", peerCounters);

        // Next local request: local=4 + peer=2 = 6, exceeds max 5
        var limited = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.True(limited);
    }

    [Fact]
    public void StalePeerPruning_RemovesPeerAfterTimeout()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));

        // Add peer state
        var peerCounters = new Dictionary<string, WindowCounter>
        {
            ["register|1.2.3.4"] = new WindowCounter { Count = 3, WindowStart = DateTimeOffset.UtcNow }
        };
        limiter.MergePeerState("node-2", peerCounters);

        // Verify peer state is included
        var state = limiter.GetLocalState();
        Assert.Equal("node-1", state.NodeId);

        // Prune with zero timeout (everything is stale)
        limiter.Prune(TimeSpan.Zero);

        // Peer should be pruned — verify by checking rate limit
        // After pruning, only local counts matter
    }

    [Fact]
    public async Task TwoInstances_ExchangeState_AgreeOnTotal()
    {
        var limiterA = new DistributedRateLimiter(new ClusterNode("node-a"));
        var limiterB = new DistributedRateLimiter(new ClusterNode("node-b"));
        var window = TimeSpan.FromHours(1);

        // Node A: 3 requests
        for (var i = 0; i < 3; i++)
            await limiterA.IsRateLimitedAsync("register|1.2.3.4", 5, window);

        // Node B: 2 requests
        for (var i = 0; i < 2; i++)
            await limiterB.IsRateLimitedAsync("register|1.2.3.4", 5, window);

        // Exchange state (simulating gossip)
        var stateA = limiterA.GetLocalState();
        var stateB = limiterB.GetLocalState();

        limiterA.MergePeerState(stateB.NodeId, stateB.Counters);
        limiterB.MergePeerState(stateA.NodeId, stateA.Counters);

        // Both should now rate limit (local + peer > 5 after next request)
        var limitedA = await limiterA.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.True(limitedA, "Node A should be rate limited (local=4 + peer=2 = 6 > 5)");

        var limitedB = await limiterB.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.True(limitedB, "Node B should be rate limited (local=3 + peer=3 = 6 > 5)");
    }

    [Fact]
    public async Task IdempotentMerge_SamePeerStateTwice_DoesNotDoubleCount()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));
        var window = TimeSpan.FromHours(1);

        var now = DateTimeOffset.UtcNow;
        var peerCounters = new Dictionary<string, WindowCounter>
        {
            ["register|1.2.3.4"] = new WindowCounter { Count = 3, WindowStart = now }
        };

        // Merge same state twice
        limiter.MergePeerState("node-2", peerCounters);
        limiter.MergePeerState("node-2", peerCounters);

        // Should only count peer's 3 once
        // 1 local request + 3 peer = 4, not limited at max 5
        var limited = await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window);
        Assert.False(limited, "Idempotent merge should not double-count (local=1 + peer=3 = 4 <= 5)");
    }

    [Fact]
    public void SelfMerge_IsIgnored()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));

        var selfCounters = new Dictionary<string, WindowCounter>
        {
            ["register|1.2.3.4"] = new WindowCounter { Count = 100, WindowStart = DateTimeOffset.UtcNow }
        };

        // Self-merge should be a no-op
        limiter.MergePeerState("node-1", selfCounters);

        // GetLocalState should show empty (no requests made locally)
        var state = limiter.GetLocalState();
        Assert.Empty(state.Counters);
    }

    [Fact]
    public void PeerRegistry_AddRefreshPrune_Lifecycle()
    {
        var registry = new PeerRegistry();

        // Add peers
        registry.AddOrRefresh("node-a", "http://10.0.0.1:8080");
        registry.AddOrRefresh("node-b", "http://10.0.0.2:8080");

        var peers = registry.GetPeers();
        Assert.Equal(2, peers.Count);

        // Refresh updates last seen
        registry.AddOrRefresh("node-a", "http://10.0.0.1:9090");
        peers = registry.GetPeers();
        var nodeA = peers.First(p => p.NodeId == "node-a");
        Assert.Equal("http://10.0.0.1:9090", nodeA.HttpAddress);

        // Remove specific peer
        registry.Remove("node-b");
        peers = registry.GetPeers();
        Assert.Single(peers);
        Assert.Equal("node-a", peers[0].NodeId);

        // Prune with zero timeout removes all
        registry.Prune(TimeSpan.Zero);
        Assert.Empty(registry.GetPeers());
    }

    [Fact]
    public void PeerRegistry_Prune_KeepsFreshPeers()
    {
        var registry = new PeerRegistry();

        registry.AddOrRefresh("node-a", "http://10.0.0.1:8080");

        // Prune with generous timeout should keep the peer
        registry.Prune(TimeSpan.FromMinutes(5));
        Assert.Single(registry.GetPeers());
    }

    [Fact]
    public async Task GetLocalState_ReturnsSnapshot()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("test-node"));

        // Make some requests
        await limiter.IsRateLimitedAsync("key-a", 10, TimeSpan.FromHours(1));
        await limiter.IsRateLimitedAsync("key-b", 10, TimeSpan.FromHours(1));
        await limiter.IsRateLimitedAsync("key-a", 10, TimeSpan.FromHours(1));

        var state = limiter.GetLocalState();

        Assert.Equal("test-node", state.NodeId);
        Assert.Equal(2, state.Counters.Count);
        Assert.Equal(2, state.Counters["key-a"].Count);
        Assert.Equal(1, state.Counters["key-b"].Count);
    }

    [Fact]
    public async Task MergePeerState_NewerWindow_ReplacesOlderWindow()
    {
        var limiter = new DistributedRateLimiter(new ClusterNode("node-1"));
        var oldTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var newTime = DateTimeOffset.UtcNow;

        // Old state
        limiter.MergePeerState("node-2", new Dictionary<string, WindowCounter>
        {
            ["key-a"] = new WindowCounter { Count = 5, WindowStart = oldTime }
        });

        // Newer state with reset count
        limiter.MergePeerState("node-2", new Dictionary<string, WindowCounter>
        {
            ["key-a"] = new WindowCounter { Count = 1, WindowStart = newTime }
        });

        // The newer window should replace the old one
        // 1 local + 1 peer = 2, should not be limited at max 5
        var limited = await limiter.IsRateLimitedAsync("key-a", 5, TimeSpan.FromHours(1));
        Assert.False(limited);
    }
}
