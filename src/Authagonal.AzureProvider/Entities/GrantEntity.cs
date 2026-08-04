using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class GrantEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string GrantRowKey = "grant";

    // The plaintext grant key (raw refresh-token / device-code handle) is deliberately NOT persisted:
    // only its SHA-256 lives in PartitionKey. Storing the handle would let a table dump replay live
    // tokens. Every lookup re-hashes the caller-supplied key, so nothing reads the handle back.
    public required string Type { get; set; }
    public string? SubjectId { get; set; }
    public required string ClientId { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>The sign-in session this grant was minted under. Null on rows written before this column.</summary>
    public string? SessionId { get; set; }

    public static GrantEntity FromModel(PersistedGrant grant, string hashedKey) => new()
    {
        PartitionKey = hashedKey,
        RowKey = GrantRowKey,
        Type = grant.Type,
        SubjectId = grant.SubjectId,
        ClientId = grant.ClientId,
        Data = grant.Data,
        CreatedAt = grant.CreatedAt,
        ExpiresAt = grant.ExpiresAt,
        ConsumedAt = grant.ConsumedAt,
        SessionId = grant.SessionId,
    };

    public PersistedGrant ToModel() => new()
    {
        Key = string.Empty, // not persisted (see note above); no read path consumes it
        Type = Type,
        SubjectId = SubjectId,
        ClientId = ClientId,
        Data = Data,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        ConsumedAt = ConsumedAt,
        SessionId = SessionId,
    };
}
