using Authagonal.Storage.Entities;

namespace Authagonal.Tests;

public class UserEmailEntityTests
{
    [Fact]
    public void Create_SetsPartitionKeyToNormalizedEmail()
    {
        var entity = UserEmailEntity.Create("ALICE@EXAMPLE.COM", "user-1");
        Assert.Equal("ALICE@EXAMPLE.COM", entity.PartitionKey);
    }

    [Fact]
    public void Create_SetsRowKeyToLookup()
    {
        var entity = UserEmailEntity.Create("BOB@EXAMPLE.COM", "user-2");
        Assert.Equal(UserEmailEntity.LookupRowKey, entity.RowKey);
    }

    [Fact]
    public void Create_SetsUserId()
    {
        var entity = UserEmailEntity.Create("CAROL@EXAMPLE.COM", "user-3");
        Assert.Equal("user-3", entity.UserId);
    }
}
