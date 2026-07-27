using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

/// <summary>
/// Reverse index from a role to the users holding it: one row per membership, partitioned by role.
/// </summary>
/// <remarks>
/// <para>
/// Roles live as a list on the user, which answers "what does this person hold" and cannot answer
/// "who holds this" without reading every user. That is the question an admin console asks on every
/// page load, and the shape of the answer without an index — scan everyone, filter in memory — is
/// wrong at any size worth having.
/// </para>
/// <para>
/// Unlike the email and name indexes, the key is NOT tokenized. A role name is a system identifier
/// an operator chose (<c>staff-admin</c>), not personal data, so there is nothing to blind — and
/// leaving it in the clear keeps the partition directly queryable and the rows self-describing when
/// someone is staring at the table trying to work out why an authorization decision went the way it
/// did.
/// </para>
/// <para>
/// Membership is naturally small and bounded per role, so one partition per role is the right grain:
/// listing a role is a single-partition query, and the write rate on any one role is the rate at
/// which people are granted it.
/// </para>
/// </remarks>
public sealed class UserRoleEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string UserId { get; set; }

    /// <summary>The role name as configured, kept for display — <see cref="PartitionKey"/> is normalized.</summary>
    public required string RoleName { get; set; }

    /// <summary>
    /// Upper-cased so lookups are case-insensitive, matching how every other normalized key in this
    /// store is formed. Role names are compared ordinally elsewhere, so this only affects the index.
    /// </summary>
    public static string Normalize(string roleName) => roleName.Trim().ToUpperInvariant();

    public static UserRoleEntity Create(string roleName, string userId) => new()
    {
        PartitionKey = Normalize(roleName),
        RowKey = userId,
        UserId = userId,
        RoleName = roleName,
    };
}
