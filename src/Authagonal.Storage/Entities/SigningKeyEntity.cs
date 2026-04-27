using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class SigningKeyEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string SigningPartitionKey = "signing";

    public required string Algorithm { get; set; }
    public required string KeyMaterialJson { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static SigningKeyEntity FromModel(SigningKeyInfo key) => new()
    {
        PartitionKey = SigningPartitionKey,
        RowKey = key.KeyId,
        Algorithm = key.Algorithm,
        KeyMaterialJson = key.KeyMaterialJson,
        IsActive = key.IsActive,
        CreatedAt = key.CreatedAt,
        ExpiresAt = key.ExpiresAt,
    };

    public SigningKeyInfo ToModel() => new()
    {
        KeyId = RowKey,
        Algorithm = Algorithm,
        KeyMaterialJson = KeyMaterialJson,
        IsActive = IsActive,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
    };
}
