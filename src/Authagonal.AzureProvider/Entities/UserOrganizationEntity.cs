using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.AzureProvider.Entities;

/// <summary>
/// Reverse index from an organization to the users placed in it: one row per membership,
/// partitioned by organization.
/// </summary>
/// <remarks>
/// <para>
/// <c>OrganizationId</c> lives as a single column on the user profile, which answers "which
/// organization is this person in" and cannot answer "who is in this organization" without reading
/// every user. The list endpoint asked the second question in the shape of the first: page the whole
/// tenant, decrypt every profile, keep the matches. That is slow, and on a tenant of any size it is
/// also WRONG — the scan gives up after a bounded number of pages, so a sparse organization comes
/// back short with no indication that the answer was truncated.
/// </para>
/// <para>
/// Like <see cref="UserRoleEntity"/>, and unlike the email and name indexes, the key is NOT a blind
/// index. An organization id is an identifier the operator's own provisioning app chose, not personal
/// data, and <c>TableUserStore</c> already keeps <c>OrganizationId</c> in the clear on the profile row
/// for exactly that reason. Blinding the index while the source column stays readable would protect
/// nothing.
/// </para>
/// <para>
/// It is HASHED rather than used verbatim, which is the one place this differs from the role index.
/// A role name is chosen by an operator in our own admin UI; an organization id arrives from a
/// customer's provisioning app as free text, and Azure Table forbids <c>/</c>, <c>\</c>, <c>#</c>,
/// <c>?</c> and control characters in a key. A downstream app returning a URN or a path-shaped id
/// would otherwise fail the index write, and because the index is written inside user creation, that
/// would fail the CREATE — turning a cosmetic choice of id format into "this user cannot sign up".
/// Hashing accepts every id the profile column accepts. The id is kept verbatim in
/// <see cref="OrganizationId"/> so the table still reads as itself when someone is looking at rows.
/// </para>
/// <para>
/// Matching stays ORDINAL, because that is what the scan it replaces did: the hash is taken over the
/// id exactly as given, so <c>acme</c> and <c>ACME</c> remain distinct organizations. Making the
/// lookup case-insensitive here would silently merge two ids a customer had deliberately kept apart,
/// which is a data change disguised as a performance fix.
/// </para>
/// <para>
/// One partition per organization is the right grain: listing an organization is a single-partition
/// query, and the write rate on any one partition is the rate at which people join it.
/// </para>
/// </remarks>
public sealed class UserOrganizationEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string UserId { get; set; }

    /// <summary>
    /// The organization id exactly as assigned. <see cref="PartitionKey"/> is a hash of it, so this is
    /// the only place the readable value survives — and the only way to tell, from the table alone,
    /// which organization a partition is.
    /// </summary>
    public required string OrganizationId { get; set; }

    /// <summary>
    /// Partition key of the row asserting that this index covers every user in the table.
    /// </summary>
    /// <remarks>
    /// The index is only trustworthy once something has walked the whole user table and written the
    /// rows for accounts that predate it. Until then, a partition that comes back empty is ambiguous:
    /// the organization may genuinely have no members, or it may simply not be indexed yet — and
    /// answering "nobody" to the second case is a silently truncated list, which is the exact defect
    /// the index exists to remove. So the marker gates the read: absent, lookups fall back to the scan
    /// and stay correct; present, the index is authoritative and the scan is never taken.
    /// <para>
    /// It transitions absent to present exactly once and never back, so callers may cache the positive.
    /// A literal, not a hash: <see cref="KeyFor"/> always yields 64 hex characters, so no organization
    /// id can ever collide with it.
    /// </para>
    /// </remarks>
    public const string CoverageMarkerKey = "coverage";

    /// <summary>Row key of the <see cref="CoverageMarkerKey"/> row.</summary>
    public const string CoverageMarkerRowKey = "complete";

    /// <summary>
    /// The partition key for an organization id: lowercase hex SHA-256 of the id's UTF-8 bytes.
    /// Deterministic, ordinal, and always a legal Azure Table key whatever the id contains.
    /// </summary>
    public static string KeyFor(string organizationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(organizationId))).ToLowerInvariant();

    public static UserOrganizationEntity Create(string organizationId, string userId) => new()
    {
        PartitionKey = KeyFor(organizationId),
        RowKey = userId,
        UserId = userId,
        OrganizationId = organizationId,
    };
}
