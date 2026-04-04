using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class ScimTokenEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string LookupRowKey = "lookup";
    public const string TokenRowKeyPrefix = "scimtoken|";

    public required string TokenId { get; set; }
    public required string ClientId { get; set; }
    public required string TokenHash { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>Forward index: PK=tokenHash, RK="lookup" — O(1) auth lookup.</summary>
    public static ScimTokenEntity FromModelForward(ScimToken token) => new()
    {
        PartitionKey = token.TokenHash,
        RowKey = LookupRowKey,
        TokenId = token.TokenId,
        ClientId = token.ClientId,
        TokenHash = token.TokenHash,
        Description = token.Description,
        CreatedAt = token.CreatedAt,
        ExpiresAt = token.ExpiresAt,
        IsRevoked = token.IsRevoked,
    };

    /// <summary>Reverse index: PK=clientId, RK="scimtoken|{tokenId}" — list by client.</summary>
    public static ScimTokenEntity FromModelReverse(ScimToken token) => new()
    {
        PartitionKey = token.ClientId,
        RowKey = $"{TokenRowKeyPrefix}{token.TokenId}",
        TokenId = token.TokenId,
        ClientId = token.ClientId,
        TokenHash = token.TokenHash,
        Description = token.Description,
        CreatedAt = token.CreatedAt,
        ExpiresAt = token.ExpiresAt,
        IsRevoked = token.IsRevoked,
    };

    public ScimToken ToModel() => new()
    {
        TokenId = TokenId,
        ClientId = ClientId,
        TokenHash = TokenHash,
        Description = Description,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        IsRevoked = IsRevoked,
    };
}
