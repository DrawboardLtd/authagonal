using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>M3: the upstream-refresh-token store round-trips through Azure Table, keys per session (sid) so
/// a user's devices don't clobber each other, rotates in place, expires on read, and 404-swallows a double
/// remove.</summary>
[Collection("Azurite")]
public class UpstreamRefreshTokenStoreTests(AzuriteFixture azurite)
{
    private TableUpstreamRefreshTokenStore NewStore()
    {
        var table = new TableServiceClient(azurite.ConnectionString).GetTableClient($"urt{Guid.NewGuid():N}");
        table.CreateIfNotExists();
        return new TableUpstreamRefreshTokenStore(table, EnvPartitioner.Live);
    }

    [Fact]
    public async Task Set_Get_Rotate_Expire_Remove()
    {
        var store = NewStore();

        await store.SetAsync("user1", "conn1", "sidA", "token-1", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("token-1", await store.GetAsync("user1", "conn1", "sidA"));

        // Per-device / per-connection isolation — a different sid, connection, or user is a distinct row.
        Assert.Null(await store.GetAsync("user1", "conn1", "sidB"));
        Assert.Null(await store.GetAsync("user1", "conn2", "sidA"));
        Assert.Null(await store.GetAsync("user2", "conn1", "sidA"));

        // Rotation replaces in place.
        await store.SetAsync("user1", "conn1", "sidA", "token-2", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("token-2", await store.GetAsync("user1", "conn1", "sidA"));

        // An expired row reads as null.
        await store.SetAsync("user1", "conn1", "sidA", "token-3", DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Null(await store.GetAsync("user1", "conn1", "sidA"));

        // Remove, then a double-remove is a no-op (404-swallow).
        await store.SetAsync("user1", "conn1", "sidA", "token-4", DateTimeOffset.UtcNow.AddHours(1));
        await store.RemoveAsync("user1", "conn1", "sidA");
        Assert.Null(await store.GetAsync("user1", "conn1", "sidA"));
        await store.RemoveAsync("user1", "conn1", "sidA");
    }
}
