using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class UserExternalIdEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string LookupRowKey = "lookup";

    public required string UserId { get; set; }

    public static UserExternalIdEntity Create(string clientId, string externalId, string userId) => new()
    {
        PartitionKey = $"{clientId}|{externalId}",
        RowKey = LookupRowKey,
        UserId = userId,
    };
}
