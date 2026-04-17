using Authagonal.Storage.Entities;

namespace Authagonal.Tests;

public class GrantByExpiryEntityTests
{
    [Fact]
    public void GetDateBucket_ReturnsYyyyMmDd()
    {
        var date = new DateTimeOffset(2025, 7, 15, 14, 30, 0, TimeSpan.Zero);
        Assert.Equal("2025-07-15", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetDateBucket_ConvertsToUtc()
    {
        var date = new DateTimeOffset(2025, 3, 28, 23, 30, 0, TimeSpan.FromHours(5));
        Assert.Equal("2025-03-28", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetDateBucket_CrossesMidnightWhenConvertingToUtc()
    {
        var date = new DateTimeOffset(2025, 3, 29, 1, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal("2025-03-29", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetDateBucket_SameInstant_SameBucket()
    {
        var utc = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var eastern = utc.ToOffset(TimeSpan.FromHours(-5));

        Assert.Equal(
            GrantByExpiryEntity.GetDateBucket(utc),
            GrantByExpiryEntity.GetDateBucket(eastern));
    }

    [Fact]
    public void GetDateBucket_LeapDay()
    {
        var date = new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("2024-02-29", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetDateBucket_PadsMonthAndDay()
    {
        var date = new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("2025-01-05", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetPartitionKey_IsDateFirstThenShardSlot()
    {
        var date = new DateTimeOffset(2025, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var pk = GrantByExpiryEntity.GetPartitionKey(date, "0abc123");
        Assert.StartsWith("2025-07-15_", pk);
        Assert.Matches(@"^2025-07-15_\d$", pk);
    }

    [Fact]
    public void GetPartitionKey_SameHashedKey_AlwaysSameSlot()
    {
        var date = new DateTimeOffset(2025, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var pk1 = GrantByExpiryEntity.GetPartitionKey(date, "deadbeef00");
        var pk2 = GrantByExpiryEntity.GetPartitionKey(date, "deadbeef00");
        Assert.Equal(pk1, pk2);
    }

    [Fact]
    public void GetPartitionKey_DifferentHashesCanLandOnDifferentSlots()
    {
        var date = new DateTimeOffset(2025, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var slots = new HashSet<string>();
        for (var i = 0; i < 256; i++)
        {
            var hashedKey = i.ToString("x2") + "0000";
            slots.Add(GrantByExpiryEntity.GetPartitionKey(date, hashedKey));
        }
        Assert.Equal(GrantByExpiryEntity.ShardCount, slots.Count);
    }

    [Fact]
    public void GetCutoffUpperBound_CoversAllShardSlotsForDate()
    {
        var date = new DateTimeOffset(2025, 7, 15, 23, 59, 59, TimeSpan.Zero);
        var upper = GrantByExpiryEntity.GetCutoffUpperBound(date);

        // Every possible partition key for today must sort le the upper bound.
        for (var i = 0; i < 256; i++)
        {
            var hashedKey = i.ToString("x2") + "0000";
            var pk = GrantByExpiryEntity.GetPartitionKey(date, hashedKey);
            Assert.True(string.CompareOrdinal(pk, upper) <= 0, $"{pk} should be <= {upper}");
        }

        // Next day's partitions must sort above the upper bound.
        var tomorrow = date.AddDays(1);
        for (var i = 0; i < 256; i++)
        {
            var hashedKey = i.ToString("x2") + "0000";
            var pk = GrantByExpiryEntity.GetPartitionKey(tomorrow, hashedKey);
            Assert.True(string.CompareOrdinal(pk, upper) > 0, $"{pk} should be > {upper}");
        }
    }
}
