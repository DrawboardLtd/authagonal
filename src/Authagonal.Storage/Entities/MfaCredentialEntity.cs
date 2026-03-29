using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class MfaCredentialEntity : ITableEntity
{
    public required string PartitionKey { get; set; } // UserId
    public required string RowKey { get; set; }        // CredentialId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public int Type { get; set; }
    public string? Name { get; set; }
    public string? SecretProtected { get; set; }
    public string? PublicKeyJson { get; set; }
    public long SignCount { get; set; }
    public bool IsConsumed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public static MfaCredentialEntity FromModel(MfaCredential cred) => new()
    {
        PartitionKey = cred.UserId,
        RowKey = cred.Id,
        Type = (int)cred.Type,
        Name = cred.Name,
        SecretProtected = cred.SecretProtected,
        PublicKeyJson = cred.PublicKeyJson,
        SignCount = cred.SignCount,
        IsConsumed = cred.IsConsumed,
        CreatedAt = cred.CreatedAt,
        LastUsedAt = cred.LastUsedAt,
    };

    public MfaCredential ToModel() => new()
    {
        Id = RowKey,
        UserId = PartitionKey,
        Type = (MfaCredentialType)Type,
        Name = Name,
        SecretProtected = SecretProtected,
        PublicKeyJson = PublicKeyJson,
        SignCount = (uint)SignCount,
        IsConsumed = IsConsumed,
        CreatedAt = CreatedAt,
        LastUsedAt = LastUsedAt,
    };
}
