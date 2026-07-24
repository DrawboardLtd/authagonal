using Authagonal.Migration;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

[Collection("Azurite")]
public class MigrationStateStoreTests(AzuriteFixture azurite)
{
    private MigrationStateStore NewStore(out string version)
    {
        // Unique version per test so the shared Azurite table doesn't cross-contaminate.
        version = "v" + Guid.NewGuid().ToString("N");
        var table = new TableServiceClient(azurite.ConnectionString).GetTableClient("MigrationStateTests");
        table.CreateIfNotExists();
        return new MigrationStateStore(table);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoMarker()
    {
        var store = NewStore(out var version);
        Assert.Null(await store.GetAsync(version));
    }

    [Fact]
    public async Task Upsert_Then_Get_RoundTrips()
    {
        var store = NewStore(out var version);
        await store.UpsertAsync(new MigrationStateEntity
        {
            RowKey = version,
            Status = MigrationStateEntity.StatusCompleted,
            NodeId = "node-1",
            DryRun = false,
            StatsJson = "{\"UsersCreated\":5}",
        });

        var marker = await store.GetAsync(version);
        Assert.NotNull(marker);
        Assert.Equal(MigrationStateEntity.PartitionKeyValue, marker!.PartitionKey);
        Assert.Equal(version, marker.RowKey);
        Assert.Equal("node-1", marker.NodeId);
        Assert.Equal("{\"UsersCreated\":5}", marker.StatsJson);
    }

    [Fact]
    public async Task CompletedRealRun_BlocksRerun()
    {
        var store = NewStore(out var version);
        await store.UpsertAsync(new MigrationStateEntity
        {
            RowKey = version,
            Status = MigrationStateEntity.StatusCompleted,
            DryRun = false,
        });
        Assert.True((await store.GetAsync(version))!.BlocksRerun);
    }

    [Theory]
    [InlineData(MigrationStateEntity.StatusCompleted, true)]   // dry run completed → does NOT block
    [InlineData(MigrationStateEntity.StatusStarted, false)]
    [InlineData(MigrationStateEntity.StatusFailed, false)]
    public async Task NonCompletedOrDryRun_DoesNotBlockRerun(string status, bool dryRun)
    {
        var store = NewStore(out var version);
        await store.UpsertAsync(new MigrationStateEntity { RowKey = version, Status = status, DryRun = dryRun });
        Assert.False((await store.GetAsync(version))!.BlocksRerun);
    }
}
