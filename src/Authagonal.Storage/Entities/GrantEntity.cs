using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class GrantEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string GrantRowKey = "grant";

    public required string Key { get; set; }
    public required string Type { get; set; }
    public string? SubjectId { get; set; }
    public required string ClientId { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public static GrantEntity FromModel(PersistedGrant grant, string hashedKey) => new()
    {
        PartitionKey = hashedKey,
        RowKey = GrantRowKey,
        Key = grant.Key,
        Type = grant.Type,
        SubjectId = grant.SubjectId,
        ClientId = grant.ClientId,
        Data = grant.Data,
        CreatedAt = grant.CreatedAt,
        ExpiresAt = grant.ExpiresAt,
        ConsumedAt = grant.ConsumedAt,
    };

    public PersistedGrant ToModel() => new()
    {
        Key = Key,
        Type = Type,
        SubjectId = SubjectId,
        ClientId = ClientId,
        Data = Data,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        ConsumedAt = ConsumedAt,
    };
}
