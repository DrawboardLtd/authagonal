namespace Authagonal.Server.Services;

/// <summary>
/// Whether a caller-supplied string can safely become part of a storage key, and whether an address is one.
/// </summary>
/// <remarks>
/// Shared by the user and group write paths because it was implemented on one and not the other. All three
/// SCIM <b>user</b> write paths validated <c>externalId</c>; none of the group ones did — even though the group
/// path is the one that puts the value <i>directly</i> into a PartitionKey
/// (<c>ScimGroupEntity</c>'s external-id index is keyed <c>{organizationId}|{externalId}</c>), while the user
/// path only makes it a component of a composite index.
/// <para>
/// The consequence on the group path was durable. <c>CreateAsync</c> writes the group row first and the
/// external-id index second, so a value the storage service rejects fails AFTER the group is durably created:
/// the connector sees a 500, treats it as retryable, and each retry leaves another orphan group row that no
/// externalId lookup can reach and whose id the client was never told. <c>CreateGroupAsync</c> counts owned
/// groups against <c>AuthOptions.MaxScimGroupsPerClient</c>, so once the orphans reach that number every
/// subsequent create — including well-formed ones — is refused, and group membership drives role assignment,
/// so role grants for that connector stop working with no way to recover through the SCIM API.
/// </para>
/// </remarks>
internal static class StorageKeySafety
{
    /// <summary>
    /// Characters no storage backend will accept in a key.
    /// </summary>
    /// <remarks>
    /// Azure Table Storage rejects these outright in a PartitionKey or RowKey. None of them can appear in an
    /// unquoted addr-spec either, so refusing them costs nothing an IdP would legitimately send.
    /// </remarks>
    internal static bool IsKeyHostile(char c) => c is '/' or '\\' or '#' or '?';

    /// <summary>
    /// The longest <c>externalId</c> that can be stored. Azure Table's key cap is 1024 characters and the
    /// group index prefixes the organization id, so 256 leaves room for both and is far past anything an
    /// IdP emits.
    /// </summary>
    internal const int MaxExternalIdLength = 256;

    /// <summary>
    /// True when <paramref name="value"/> is storable as (part of) a key: no control characters, none of
    /// <see cref="IsKeyHostile"/>, and within <see cref="MaxExternalIdLength"/>.
    /// </summary>
    internal static bool IsUsableExternalId(string value)
    {
        if (value.Length > MaxExternalIdLength) return false;
        foreach (var c in value)
            if (char.IsControl(c) || IsKeyHostile(c)) return false;
        return true;
    }

    /// <summary>The message every refusal uses, so the two surfaces answer identically.</summary>
    internal const string ExternalIdRefusal =
        "externalId contains characters that cannot be stored, or is too long";

    /// <summary>
    /// True when <paramref name="value"/> is plausibly an email address AND storable as a key.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the SCIM endpoints because the ADMIN user-creation path needed the identical
    /// check and had none: <c>POST /api/v1/profile</c> validated only that the address was non-empty, while both
    /// sibling creation paths (anonymous self-registration and SCIM) refuse the same values for reasons those
    /// paths document.
    /// <para>
    /// Not cosmetic on the admin path. With the default non-tokenizing configuration the normalized address IS
    /// the email index's PartitionKey, and the profile row is written BEFORE the index row — so a key the
    /// storage service rejects fails after the account is durably created, leaving a record
    /// <c>FindByEmailAsync</c> cannot reach: the user cannot log in, cannot reset their password, and the
    /// address cannot be reused because the profile row still holds it.
    /// </para>
    /// </remarks>
    internal static bool IsPlausibleEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        foreach (var c in value)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || IsKeyHostile(c)) return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1) return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
