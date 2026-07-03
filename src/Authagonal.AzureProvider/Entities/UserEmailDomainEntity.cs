using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

/// <summary>
/// Blind index for "all users at domain X" (e.g. everyone @acme.com). PartitionKey is the tokenized
/// normalized domain — a keyed HMAC when tokenization is on — so a table dump exposes no domains;
/// RowKey is the userId, one row per user, so a single partition query lists a domain's members.
/// Prefix tokens are left-anchored, so they can't answer domain/suffix questions — this dedicated
/// exact-match index is how "@company" search survives encryption.
/// </summary>
public sealed class UserEmailDomainEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string UserId { get; set; }

    /// <summary>
    /// The normalized domain of an email — the substring after the last '@' (the email is already
    /// upper-cased when it reaches here). Returns null when there is no usable domain.
    /// </summary>
    public static string? DomainOf(string? normalizedEmail)
    {
        if (string.IsNullOrEmpty(normalizedEmail)) return null;
        var at = normalizedEmail.LastIndexOf('@');
        return at >= 0 && at < normalizedEmail.Length - 1 ? normalizedEmail[(at + 1)..] : null;
    }
}
