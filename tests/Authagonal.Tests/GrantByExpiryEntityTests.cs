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
        // 2025-03-28 23:30 UTC+5 = 2025-03-28 18:30 UTC → bucket is 2025-03-28
        var date = new DateTimeOffset(2025, 3, 28, 23, 30, 0, TimeSpan.FromHours(5));
        Assert.Equal("2025-03-28", GrantByExpiryEntity.GetDateBucket(date));
    }

    [Fact]
    public void GetDateBucket_CrossesMidnightWhenConvertingToUtc()
    {
        // 2025-03-29 01:00 UTC-5 = 2025-03-29 06:00 UTC → still 2025-03-29
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
}
