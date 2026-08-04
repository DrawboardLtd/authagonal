using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

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

    /// <summary>The sign-in session this grant was minted under. Null on rows written before this column.</summary>
    public string? SessionId { get; set; }

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
        SessionId = grant.SessionId,
    };

    /// <remarks>
    /// Takes the partitioner because <c>PartitionKey</c> carries the <c>{env}|</c> prefix outside the live
    /// env, and this field is the model's IDENTITY — it is echoed to callers and fed straight back into
    /// <c>PK()</c> on the next write. Unstripped, a read-modify-write re-prefixes it and targets a phantom
    /// row: the update returns 200 and changes nothing. Stripping was done at two of the nine stores that
    /// needed it, so it is a required parameter here rather than a call-site convention.
    /// </remarks>
    public PersistedGrant ToModel(EnvPartitioner partitioner) => new()
    {
        Key = string.Empty, // not persisted (see note above); no read path consumes it
        Type = Type,
        SubjectId = partitioner.Strip(PartitionKey),
        ClientId = ClientId,
        Data = Data,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        ConsumedAt = ConsumedAt,
        SessionId = SessionId,
    };
}
