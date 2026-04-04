using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class GrantByExpiryEntity : ITableEntity
{
    public required string PartitionKey { get; set; } // yyyy-MM-dd date bucket
    public required string RowKey { get; set; }       // hashedKey
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string? SubjectId { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static string GetDateBucket(DateTimeOffset expiresAt) =>
        expiresAt.UtcDateTime.ToString("yyyy-MM-dd");
}
