using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class UserLastNameEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string AllPartitionKey = "all";

    public required string UserId { get; set; }

    public static string MakeRowKey(string normalizedLastName, string userId)
        => $"{normalizedLastName}|{userId}";

    public static UserLastNameEntity Create(string normalizedLastName, string userId) => new()
    {
        PartitionKey = AllPartitionKey,
        RowKey = MakeRowKey(normalizedLastName, userId),
        UserId = userId,
    };
}
