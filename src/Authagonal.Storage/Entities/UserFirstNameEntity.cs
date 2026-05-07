using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class UserFirstNameEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// Number of leading characters of the normalized name that form the
    /// PartitionKey. With 2, a population of 1M users spreads across ~200–400
    /// active partitions in real distributions (a few thousand rows per active
    /// PK), lifting the write ceiling from ~2k ops/sec (one global "all"
    /// partition) to N×2k ops/sec. Names shorter than this length use the whole
    /// name as their PK; SearchAsync requires a query of at least this many
    /// chars to use the name index (callers can require a min input length on
    /// admin search UIs to keep queries single-partition).
    /// </summary>
    public const int PartitionKeyLength = 2;

    public required string UserId { get; set; }

    /// <summary>
    /// PartitionKey for a normalized name. Returns the first
    /// <see cref="PartitionKeyLength"/> chars, or the whole name if shorter.
    /// Caller is responsible for normalization (upper-cased trim).
    /// </summary>
    public static string GetPartitionKey(string normalizedName)
        => normalizedName.Length >= PartitionKeyLength
            ? normalizedName[..PartitionKeyLength]
            : normalizedName;

    public static string MakeRowKey(string normalizedFirstName, string userId)
        => $"{normalizedFirstName}|{userId}";

    public static UserFirstNameEntity Create(string normalizedFirstName, string userId) => new()
    {
        PartitionKey = GetPartitionKey(normalizedFirstName),
        RowKey = MakeRowKey(normalizedFirstName, userId),
        UserId = userId,
    };
}
