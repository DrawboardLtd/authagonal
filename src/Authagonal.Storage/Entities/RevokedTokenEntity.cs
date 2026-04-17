using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class RevokedTokenEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string RevokedPartition = "revoked";

    public DateTimeOffset ExpiresAt { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset RevokedAt { get; set; }
}
