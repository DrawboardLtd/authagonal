using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

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

    /// <summary>
    /// <see cref="ScimToken.AllowedEmailDomains"/>, space-delimited — Table Storage has no list type. A
    /// domain cannot contain whitespace, so the delimiter is unambiguous. Null on rows written before this
    /// column, which reads back as empty, i.e. unrestricted: the previous behaviour.
    /// </summary>
    public string? AllowedEmailDomains { get; set; }

    /// <summary><see cref="ScimToken.OrganizationId"/>. Null on rows written before this column, which
    /// reads back as "untagged" — the previous behaviour.</summary>
    public string? OrganizationId { get; set; }

    private static string? Pack(List<string> domains) =>
        domains.Count == 0 ? null : string.Join(' ', domains);

    private static List<string> Unpack(string? packed) =>
        string.IsNullOrEmpty(packed)
            ? []
            : [.. packed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

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
        AllowedEmailDomains = Pack(token.AllowedEmailDomains),
        OrganizationId = token.OrganizationId,
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
        AllowedEmailDomains = Pack(token.AllowedEmailDomains),
        OrganizationId = token.OrganizationId,
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
        AllowedEmailDomains = Unpack(AllowedEmailDomains),
        OrganizationId = OrganizationId,
    };
}
