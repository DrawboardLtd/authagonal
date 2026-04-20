using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class UserFirstNameEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string AllPartitionKey = "all";

    public required string UserId { get; set; }

    public static string MakeRowKey(string normalizedFirstName, string userId)
        => $"{normalizedFirstName}|{userId}";

    public static UserFirstNameEntity Create(string normalizedFirstName, string userId) => new()
    {
        PartitionKey = AllPartitionKey,
        RowKey = MakeRowKey(normalizedFirstName, userId),
        UserId = userId,
    };
}
