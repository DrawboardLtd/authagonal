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

    /// <summary>
    /// A node deliberately outside a cluster is actually leader.
    /// </summary>
    /// <remarks>
    /// Standalone mode was <c>Update(true, Timeout.InfiniteTimeSpan)</c>, and that value is
    /// <c>new TimeSpan(0, 0, 0, 0, -1)</c> — a NEGATIVE duration. So the deadline landed a millisecond in
    /// the past, <c>IsLeader</c> was false forever, and the log line said "running standalone as permanent
    /// leader". Every leader-gated job silently never ran on a node with <c>Cluster:Enabled=false</c>:
    /// signing-key generation, the expiry reaper, grant reconciliation. The local-expiry guard added to
    /// protect against a stalled loop turned the permanent-leader sentinel into permanent non-leadership.
    /// </remarks>
    [Fact]
    public void LeaderElection_PermanentLeader_StaysLeader()
    {
        var e = new LeaderElection("node-1");

        e.MarkPermanentLeader();

        Assert.True(e.IsLeader);
        Assert.Equal("node-1", e.LeaderId);
    }

    /// <summary>The sentinel that caused it: a negative TimeSpan is a deadline in the past.</summary>
    /// <remarks>
    /// Pinned as a property of the arithmetic rather than of the old call site, so nobody reintroduces it by
    /// reaching for <c>Timeout.InfiniteTimeSpan</c> in a date calculation somewhere else.
    /// </remarks>
    [Fact]
    public void LeaderElection_InfiniteTimeSpanIsNotAPermanentLease()
    {
        var e = new LeaderElection("node-1");

        e.Update(true, Timeout.InfiniteTimeSpan);

        Assert.False(e.IsLeader);
    }

    /// <summary>
    /// The lease is dated from when the renewal was issued, not from when the response came back.
    /// </summary>
    /// <remarks>
    /// The backend's lease expires <c>ttl</c> after the BACKEND granted it, so computing the local deadline
    /// on return overshoots the real expiry by the whole round-trip time — and the documentation asserted the
    /// opposite ("a slow renewal shortens the window rather than extending it"). A stalled lease-store call
    /// is exactly the case the local deadline exists to contain, so the old direction meant the longer the
    /// call hung, the further past the real expiry this node kept claiming leadership.
    /// </remarks>
    [Fact]
    public void LeaderElection_SlowRenewalShortensTheWindowRatherThanExtendingIt()
    {
        var e = new LeaderElection("node-1");
        var ttl = TimeSpan.FromSeconds(30);

        // A renewal issued 29 seconds ago that has only just returned: one second of lease left.
        e.Update(true, DateTimeOffset.UtcNow.AddSeconds(-29), ttl);
        Assert.True(e.IsLeader);

        // And one whose round trip outlasted the lease entirely: not leader, with no special case.
        e.Update(true, DateTimeOffset.UtcNow.AddSeconds(-31), ttl);
        Assert.False(e.IsLeader);
        Assert.Null(e.LeaderId);
    }

    // ----- NeverLeaseProvider --------------------------------------------------

    /// <summary>A bus-only node never wins the lease.</summary>
    /// <remarks>
    /// <c>UseAzureStorageBus</c>, <c>UseAwsDynamoBus</c> and <c>UseSqlBus</c> are documented as being for a
    /// node that must receive cluster events but must never hold leadership — and they left
    /// <c>InProcessLeaseProvider</c> in place, which grants unconditionally. So such a node became leader on
    /// its first tick and stayed leader, while a real cluster node held the distributed lease as well.
    /// </remarks>
    [Fact]
    public async Task NeverLease_IsNeverGranted()
    {
        var provider = new NeverLeaseProvider();

        Assert.False(await provider.TryAcquireOrRenewAsync("authagonal-leader", "node-1", TimeSpan.FromSeconds(30)));
        Assert.False(await provider.TryAcquireOrRenewAsync("authagonal-leader", "node-1", TimeSpan.FromSeconds(30)));

        // Release is a no-op rather than a throw: the election loop calls it on shutdown regardless.
        await provider.ReleaseAsync("authagonal-leader", "node-1");
    }

    /// <summary>
    /// The three bus-only helpers all replace the lease provider, so none of them can drift back.
    /// </summary>
    /// <remarks>
    /// A source check rather than a wiring test, because exercising all three needs a live Azure, DynamoDB
    /// and SQL client. What matters is that the always-granting default is displaced in each — the defect was
    /// identical in all three and would be reintroduced the same way.
    /// </remarks>
    [Fact]
    public void EveryBusOnlyHelperReplacesTheAlwaysGrantingLease()
    {
        (string File, string Method)[] helpers =
        [
            ("src/Authagonal.AzureProvider/Clustering/ClusteringAzureExtensions.cs", "UseAzureStorageBus"),
            ("src/Authagonal.AwsProvider/Clustering/ClusteringAwsExtensions.cs", "UseAwsDynamoBus"),
            ("src/Authagonal.SqlProvider/Clustering/ClusteringSqlExtensions.cs", "UseSqlBus"),
        ];

        foreach (var (file, method) in helpers)
        {
            var path = Path.Combine(RepositoryRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"expected {path}");

            var text = File.ReadAllText(path);
            var start = text.IndexOf($"ClusteringBuilder {method}(", StringComparison.Ordinal);
            Assert.True(start > 0, $"{method} not found in {file}");

            // The method body up to the next public member.
            var end = text.IndexOf("    public static ", start + 1, StringComparison.Ordinal);
            var body = end > start ? text[start..end] : text[start..];

            Assert.Contains("NeverLeaseProvider", body, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
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
