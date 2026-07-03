using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class GrantBySubjectEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Plaintext grant key is not persisted here either (see GrantEntity); HashedKey is the SHA-256
    // used to locate the primary grant row for index-cleanup deletes.
    public required string HashedKey { get; set; }
    public required string Type { get; set; }
    public required string ClientId { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public static GrantBySubjectEntity FromModel(PersistedGrant grant, string hashedKey) => new()
    {
        PartitionKey = grant.SubjectId ?? string.Empty,
        RowKey = $"{grant.Type}|{hashedKey}",
        HashedKey = hashedKey,
        Type = grant.Type,
        ClientId = grant.ClientId,
        Data = grant.Data,
        CreatedAt = grant.CreatedAt,
        ExpiresAt = grant.ExpiresAt,
        ConsumedAt = grant.ConsumedAt,
    };

    public PersistedGrant ToModel() => new()
    {
        Key = string.Empty, // not persisted (see note above); no read path consumes it
        Type = Type,
        SubjectId = PartitionKey,
        ClientId = ClientId,
        Data = Data,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        ConsumedAt = ConsumedAt,
    };
}
