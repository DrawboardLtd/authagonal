namespace Authagonal.Server.Endpoints.Scim;

/// <summary>
/// Whether a SCIM-supplied string can safely become part of a storage key.
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
internal static class ScimKeySafety
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
}
