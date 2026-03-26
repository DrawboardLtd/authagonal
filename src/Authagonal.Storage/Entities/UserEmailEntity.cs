using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class UserEmailEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string LookupRowKey = "lookup";

    public required string UserId { get; set; }

    public static UserEmailEntity Create(string normalizedEmail, string userId) => new()
    {
        PartitionKey = normalizedEmail,
        RowKey = LookupRowKey,
        UserId = userId,
    };
}
