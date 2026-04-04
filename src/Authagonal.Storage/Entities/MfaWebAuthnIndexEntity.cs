using Azure;
using Azure.Data.Tables;

namespace Authagonal.Storage.Entities;

public sealed class MfaWebAuthnIndexEntity : ITableEntity
{
    public required string PartitionKey { get; set; } // SHA256(webAuthnCredentialId) hex
    public required string RowKey { get; set; }        // "lookup"
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string LookupRowKey = "lookup";

    public required string UserId { get; set; }
    public required string CredentialId { get; set; }
}
