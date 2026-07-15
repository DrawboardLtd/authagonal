using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Clustering;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Integration tests for the DynamoDB clustering backend, run against DynamoDB Local — the AWS
/// counterpart of <see cref="ClusteringAzureTests"/>. These exercise the real conditional-write
/// lease mechanics (acquire/renew/steal-after-expiry/single-holder) and the append-only table
/// event bus (publish → poll receive, topic isolation, no boot-time replay).
/// </summary>
[Collection("Dynamo")]
public class DynamoClusteringTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoLeaseProvider> NewLeaseProviderAsync(string table)
    {
        await DynamoTableProvisioner.EnsureTableAsync(_db, table);
        return new DynamoLeaseProvider(_db, table, NullLogger<DynamoLeaseProvider>.Instance);
    }

    private async Task<DynamoClusterEventBus> NewBusAsync(string table, TimeSpan poll)
    {
        await DynamoTableProvisioner.EnsureTableAsync(_db, table);
        return new DynamoClusterEventBus(new DynamoTable(_db, table), poll, NullLogger<DynamoClusterEventBus>.Instance);
    }

    // ----- DynamoLeaseProvider (leader election) ---------------------------------

    [Fact]
    public async Task Lease_SingleHolder_ContendsAndTransfers()
    {
        var table = "clLeaseContend";
        var ttl = TimeSpan.FromSeconds(15);
        var nodeA = await NewLeaseProviderAsync(table);
        var nodeB = await NewLeaseProviderAsync(table);

        // A acquires; B (different node) cannot while A holds it.
        Assert.True(await nodeA.TryAcquireOrRenewAsync("leader", "A", ttl));
        Assert.False(await nodeB.TryAcquireOrRenewAsync("leader", "B", ttl));

        // A can renew while it holds the lease.
        Assert.True(await nodeA.TryAcquireOrRenewAsync("leader", "A", ttl));

        // B releasing a lease it doesn't hold is a no-op — A still holds it.
        await nodeB.ReleaseAsync("leader", "B");
        Assert.False(await nodeB.TryAcquireOrRenewAsync("leader", "B", ttl));

        // After A releases, B can take over.
        await nodeA.ReleaseAsync("leader", "A");
        Assert.True(await nodeB.TryAcquireOrRenewAsync("leader", "B", ttl));

        await nodeB.ReleaseAsync("leader", "B");
    }

    [Fact]
    public async Task Lease_ExpiredLease_CanBeStolen()
    {
        var table = "clLeaseSteal";
        var nodeA = await NewLeaseProviderAsync(table);
        var nodeB = await NewLeaseProviderAsync(table);

        Assert.True(await nodeA.TryAcquireOrRenewAsync("leader", "A", TimeSpan.FromMilliseconds(300)));
        Assert.False(await nodeB.TryAcquireOrRenewAsync("leader", "B", TimeSpan.FromSeconds(15)));

        // Once A's TTL lapses without renewal, B's conditional write wins the takeover…
        await Task.Delay(600);
        Assert.True(await nodeB.TryAcquireOrRenewAsync("leader", "B", TimeSpan.FromSeconds(15)));

        // …and the deposed A can neither re-acquire nor release B's live lease.
        Assert.False(await nodeA.TryAcquireOrRenewAsync("leader", "A", TimeSpan.FromSeconds(15)));
        await nodeA.ReleaseAsync("leader", "A");
        Assert.False(await nodeA.TryAcquireOrRenewAsync("leader", "A", TimeSpan.FromSeconds(15)));

        await nodeB.ReleaseAsync("leader", "B");
    }

    [Fact]
    public async Task Lease_DistinctResources_AreIndependent()
    {
        var table = "clLeaseMulti";
        var nodeA = await NewLeaseProviderAsync(table);
        var nodeB = await NewLeaseProviderAsync(table);
        var ttl = TimeSpan.FromSeconds(15);

        // Different resources have their own lease rows; the same table serves them all.
        Assert.True(await nodeA.TryAcquireOrRenewAsync("backup", "A", ttl));
        Assert.True(await nodeB.TryAcquireOrRenewAsync("cleanup", "B", ttl));
        Assert.False(await nodeB.TryAcquireOrRenewAsync("backup", "B", ttl));

        await nodeA.ReleaseAsync("backup", "A");
        await nodeB.ReleaseAsync("cleanup", "B");
    }

    // ----- DynamoClusterEventBus (cross-node fan-out) ----------------------------

    [Fact]
    public async Task Bus_DeliversPublishedEventsToOtherNode()
    {
        var poll = TimeSpan.FromMilliseconds(200);
        var publisher = await NewBusAsync("clBusDeliver", poll);
        var subscriber = await NewBusAsync("clBusDeliver", poll);

        var received = new List<string>();
        subscriber.Subscribe("tenant-invalidate", (payload, _) =>
        {
            lock (received) received.Add(System.Text.Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        });

        await subscriber.StartAsync(CancellationToken.None);
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync("tenant-invalidate", System.Text.Encoding.UTF8.GetBytes("acme"));
            await WaitForAsync(() => { lock (received) return received.Contains("acme"); }, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bus_OnlyDeliversToSubscribedTopic()
    {
        var poll = TimeSpan.FromMilliseconds(200);
        var bus = await NewBusAsync("clBusTopics", poll);

        var hits = 0;
        bus.Subscribe("wanted", (_, _) => { Interlocked.Increment(ref hits); return Task.CompletedTask; });

        await bus.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync("unwanted", ReadOnlyMemory<byte>.Empty);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Equal(0, Volatile.Read(ref hits));

            await bus.PublishAsync("wanted", ReadOnlyMemory<byte>.Empty);
            await WaitForAsync(() => Volatile.Read(ref hits) == 1, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bus_DoesNotReplayEventsPublishedBeforeStart()
    {
        var poll = TimeSpan.FromMilliseconds(200);
        var publisher = await NewBusAsync("clBusNoReplay", poll);
        var subscriber = await NewBusAsync("clBusNoReplay", poll);

        // An event already in the log when a node boots must not be replayed to it.
        await publisher.PublishAsync("topic", System.Text.Encoding.UTF8.GetBytes("historic"));

        var received = new List<string>();
        subscriber.Subscribe("topic", (payload, _) =>
        {
            lock (received) received.Add(System.Text.Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        });

        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            lock (received) Assert.Empty(received);

            // Events published after the subscriber's start do arrive.
            await publisher.PublishAsync("topic", System.Text.Encoding.UTF8.GetBytes("fresh"));
            await WaitForAsync(() => { lock (received) return received.Contains("fresh"); }, TimeSpan.FromSeconds(15));
            lock (received) Assert.DoesNotContain("historic", received);
        }
        finally
        {
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Condition not met within timeout");
    }
}
