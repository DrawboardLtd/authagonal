using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

public sealed class GrantByExpiryEntity : ITableEntity
{
    // Hash-spread factor. Writes per date-bucket spread across ShardCount partitions,
    // lifting the per-tenant partition ceiling from 2k ops/sec to ShardCount * 2k ops/sec.
    // Keep date as PK prefix so RemoveExpiredAsync can still do a single lexicographic
    // range scan (PartitionKey le '<cutoff>_<ShardCount-1>').
    // 4 gives ~8k ops/sec headroom — comfortable for 1M+ MAU. GetShardSlot() takes
    // the first hex nibble (0-15) of the hashed key so any ShardCount up to 16
    // distributes evenly.
    public const int ShardCount = 4;

    public required string PartitionKey { get; set; } // "yyyy-MM-dd_N" where N = 0..ShardCount-1
    public required string RowKey { get; set; }       // hashedKey
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string? SubjectId { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static string GetPartitionKey(DateTimeOffset expiresAt, string hashedKey) =>
        $"{GetDateBucket(expiresAt)}_{GetShardSlot(hashedKey)}";

    public static string GetCutoffUpperBound(DateTimeOffset cutoff) =>
        $"{GetDateBucket(cutoff)}_{ShardCount - 1}";

    public static string GetDateBucket(DateTimeOffset expiresAt) =>
        expiresAt.UtcDateTime.ToString("yyyy-MM-dd");

    private static int GetShardSlot(string hashedKey)
    {
        var c = hashedKey[0];
        var nibble = c <= '9' ? c - '0' : c - 'a' + 10;
        return nibble % ShardCount;
    }
}
