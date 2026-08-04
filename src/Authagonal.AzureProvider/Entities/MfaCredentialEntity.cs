using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.AzureProvider.Entities;

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
    public long? LastTotpStep { get; set; }
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
        LastTotpStep = cred.LastTotpStep,
        IsConsumed = cred.IsConsumed,
        CreatedAt = cred.CreatedAt,
        LastUsedAt = cred.LastUsedAt,
    };

    /// <remarks>
    /// Takes the partitioner because <c>PartitionKey</c> carries the <c>{env}|</c> prefix outside the live
    /// env, and this field is the model's IDENTITY — it is echoed to callers and fed straight back into
    /// <c>PK()</c> on the next write. Unstripped, a read-modify-write re-prefixes it and targets a phantom
    /// row: the update returns 200 and changes nothing. Stripping was done at two of the nine stores that
    /// needed it, so it is a required parameter here rather than a call-site convention.
    /// </remarks>
    public MfaCredential ToModel(EnvPartitioner partitioner) => new()
    {
        Id = RowKey,
        UserId = partitioner.Strip(PartitionKey),
        Type = (MfaCredentialType)Type,
        Name = Name,
        SecretProtected = SecretProtected,
        PublicKeyJson = PublicKeyJson,
        SignCount = (uint)SignCount,
        LastTotpStep = LastTotpStep,
        IsConsumed = IsConsumed,
        CreatedAt = CreatedAt,
        LastUsedAt = LastUsedAt,
    };
}
