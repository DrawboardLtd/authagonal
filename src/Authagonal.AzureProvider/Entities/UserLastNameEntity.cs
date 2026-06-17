using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

public sealed class UserLastNameEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// First-N-char-prefix PartitionKey scheme — see
    /// <see cref="UserFirstNameEntity.PartitionKeyLength"/> for the rationale.
    /// </summary>
    public const int PartitionKeyLength = 2;

    public required string UserId { get; set; }

    public static string GetPartitionKey(string normalizedName)
        => normalizedName.Length >= PartitionKeyLength
            ? normalizedName[..PartitionKeyLength]
            : normalizedName;

    public static string MakeRowKey(string normalizedLastName, string userId)
        => $"{normalizedLastName}|{userId}";

    public static UserLastNameEntity Create(string normalizedLastName, string userId) => new()
    {
        PartitionKey = GetPartitionKey(normalizedLastName),
        RowKey = MakeRowKey(normalizedLastName, userId),
        UserId = userId,
    };
}
