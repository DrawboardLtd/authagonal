using Authagonal.Core.Clustering;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

public class ClusteringTests
{
    // ----- InProcessRateLimiter -------------------------------------------------

    [Fact]
    public async Task RateLimiter_EnforcesMax()
    {
        var limiter = new InProcessRateLimiter();
        var window = TimeSpan.FromHours(1);

        for (var i = 0; i < 5; i++)
            Assert.False(await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window), $"request {i + 1}");

        Assert.True(await limiter.IsRateLimitedAsync("register|1.2.3.4", 5, window));
    }

    [Fact]
    public async Task RateLimiter_WindowExpiry_Resets()
    {
        var limiter = new InProcessRateLimiter();
        var window = TimeSpan.FromMilliseconds(50);

        for (var i = 0; i < 5; i++)
            await limiter.IsRateLimitedAsync("k", 5, window);
        Assert.True(await limiter.IsRateLimitedAsync("k", 5, window));

        await Task.Delay(100);
        Assert.False(await limiter.IsRateLimitedAsync("k", 5, window));
    }

    [Fact]
    public async Task RateLimiter_KeysAreIndependent()
    {
        var limiter = new InProcessRateLimiter();
        var window = TimeSpan.FromHours(1);

        for (var i = 0; i < 6; i++)
            await limiter.IsRateLimitedAsync("a", 5, window);

        // Different key has its own window.
        Assert.False(await limiter.IsRateLimitedAsync("b", 5, window));
    }

    // ----- LeaderElection -------------------------------------------------------

    [Fact]
    public void LeaderElection_DefaultsToNonLeader()
    {
        var e = new LeaderElection("node-1");
        Assert.False(e.IsLeader);
        Assert.Null(e.LeaderId);
        Assert.Equal("node-1", e.NodeId);
    }

    [Fact]
    public void LeaderElection_Update_TogglesLeadership()
    {
        var e = new LeaderElection("node-1");

        e.Update(true, TimeSpan.FromMinutes(1));
        Assert.True(e.IsLeader);
        Assert.Equal("node-1", e.LeaderId);

        e.Update(false, TimeSpan.Zero);
        Assert.False(e.IsLeader);
        Assert.Null(e.LeaderId);
    }

    [Fact]
    public void LeaderElection_StopsClaimingLeadershipOnceTheLeaseLapses()
    {
        // Leadership used to be whatever the election loop last wrote, with no expiry of its own — so
        // a node whose loop stalled (a GC pause, a blocked thread, a hung lease-store call) kept
        // answering true long after the lease had lapsed and another node had taken it. Both then
        // believed they were leader, which is what the election exists to prevent: the guarded work is
        // signing-key generation and the expiry reaper.
        var e = new LeaderElection("node-1");

        e.Update(true, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);

        Assert.False(e.IsLeader);
        Assert.Null(e.LeaderId);
    }

    // ----- InProcessLeaseProvider ----------------------------------------------

    [Fact]
    public async Task InProcessLease_AlwaysGranted()
    {
        var lease = new InProcessLeaseProvider();
        Assert.True(await lease.TryAcquireOrRenewAsync("r", "node-1", TimeSpan.FromSeconds(30)));
        Assert.True(await lease.TryAcquireOrRenewAsync("r", "node-1", TimeSpan.FromSeconds(30)));
        await lease.ReleaseAsync("r", "node-1"); // no-op, must not throw
    }

    // ----- InProcessClusterEventBus --------------------------------------------

    [Fact]
    public async Task InProcessBus_DeliversToSubscribers()
    {
        var bus = new InProcessClusterEventBus();
        var received = new List<string>();

        using var _ = bus.Subscribe("t", (payload, _) =>
        {
            received.Add(System.Text.Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        });

        await bus.PublishAsync("t", System.Text.Encoding.UTF8.GetBytes("hello"));
        await bus.PublishAsync("other", System.Text.Encoding.UTF8.GetBytes("ignored"));

        Assert.Equal(new[] { "hello" }, received);
    }

    [Fact]
    public async Task InProcessBus_Unsubscribe_StopsDelivery()
    {
        var bus = new InProcessClusterEventBus();
        var count = 0;

        var sub = bus.Subscribe("t", (_, _) => { count++; return Task.CompletedTask; });
        await bus.PublishAsync("t", ReadOnlyMemory<byte>.Empty);
        sub.Dispose();
        await bus.PublishAsync("t", ReadOnlyMemory<byte>.Empty);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task InProcessBus_FaultingHandler_DoesNotBlockOthers()
    {
        var bus = new InProcessClusterEventBus();
        var reached = false;

        using var _ = bus.Subscribe("t", (_, _) => throw new InvalidOperationException("boom"));
        using var __ = bus.Subscribe("t", (_, _) => { reached = true; return Task.CompletedTask; });

        await bus.PublishAsync("t", ReadOnlyMemory<byte>.Empty);

        Assert.True(reached);
    }
}
