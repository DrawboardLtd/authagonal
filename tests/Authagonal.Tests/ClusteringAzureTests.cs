using Authagonal.Storage.Clustering;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Integration tests for the Azure-storage clustering backend, run against Azurite. These exercise
/// the real blob-lease and table-poll mechanics that replace multicast + headless-DNS fan-out.
/// The blob client is pinned to an older REST API version because the latest SDK negotiates a
/// version newer than the Azurite image recognises; production passes its own (latest) client.
/// </summary>
[Collection("Azurite")]
public class ClusteringAzureTests(AzuriteFixture azurite)
{
    private static readonly BlobClientOptions BlobOptions = new(BlobClientOptions.ServiceVersion.V2024_11_04);

    private BlobLeaseProvider NewLeaseProvider(string container) =>
        new(new BlobServiceClient(azurite.ConnectionString, BlobOptions), container,
            NullLogger<BlobLeaseProvider>.Instance);

    private TableClusterEventBus NewBus(string table, TimeSpan poll) =>
        new(new TableServiceClient(azurite.ConnectionString).GetTableClient(table), poll,
            NullLogger<TableClusterEventBus>.Instance);

    // ----- BlobLeaseProvider (leader election) ----------------------------------

    [Fact]
    public async Task BlobLease_SingleHolder_ContendsAndTransfers()
    {
        var container = $"lease{Guid.NewGuid():N}";
        var ttl = TimeSpan.FromSeconds(15);
        var nodeA = NewLeaseProvider(container);
        var nodeB = NewLeaseProvider(container);

        // A acquires; B (different node) cannot while A holds it.
        Assert.True(await nodeA.TryAcquireOrRenewAsync("leader", "A", ttl));
        Assert.False(await nodeB.TryAcquireOrRenewAsync("leader", "B", ttl));

        // A can renew while it holds the lease.
        Assert.True(await nodeA.TryAcquireOrRenewAsync("leader", "A", ttl));

        // After A releases, B can take over.
        await nodeA.ReleaseAsync("leader", "A");
        Assert.True(await nodeB.TryAcquireOrRenewAsync("leader", "B", ttl));

        await nodeB.ReleaseAsync("leader", "B");
    }

    // ----- TableClusterEventBus (cross-node fan-out) ----------------------------

    [Fact]
    public async Task TableBus_DeliversPublishedEventsToOtherNode()
    {
        var table = $"clusterevents{Guid.NewGuid():N}";
        var poll = TimeSpan.FromMilliseconds(200);

        var publisher = NewBus(table, poll);
        var subscriber = NewBus(table, poll);

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
    public async Task TableBus_OnlyDeliversToSubscribedTopic()
    {
        var table = $"clusterevents{Guid.NewGuid():N}";
        var poll = TimeSpan.FromMilliseconds(200);
        var bus = NewBus(table, poll);

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
