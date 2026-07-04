using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

/// <summary>
/// Blind index for "email local-part starts with X" (e.g. type "ali" → alistair@…). Each prefix of the
/// normalized local part (the bit before '@') is its own row: PartitionKey is the keyed-HMAC token of the
/// prefix, RowKey is the userId. So "starts with p" becomes an exact-match lookup on HMAC(p) — the same
/// technique the name index uses to keep prefix search working over encrypted values (HMAC destroys the
/// ordering a range scan would need). Written only when tokenization is on; with it off, email prefix
/// search uses the ordered range scan on the exact-email index instead.
/// </summary>
public sealed class UserEmailLocalPrefixEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string UserId { get; set; }
}
