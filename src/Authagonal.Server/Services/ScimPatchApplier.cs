using System.Text.Json;
using System.Text.RegularExpressions;
using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

/// <summary>
/// A PATCH operation that cannot be applied as written.
/// </summary>
/// <remarks>
/// The applier returned void and silently dropped anything it did not recognise, so the endpoint
/// answered 200 OK for operations it had not performed. For a deprovisioning PATCH that means the
/// IdP records the user as disabled while the account stays live — the failure mode is invisible on
/// both sides. Carrying a scimType lets the endpoint answer with the error RFC 7644 §3.12 defines.
/// </remarks>
public sealed class ScimPatchException(string scimType, string detail) : Exception(detail)
{
    public string ScimType { get; } = scimType;
}

public static partial class ScimPatchApplier
{
    public sealed record PatchOperation(string Op, string? Path, JsonElement? Value);

    // SCIM value-path filter carrying the member id in the PATH, e.g. members[value eq "abc-123"].
    // This is Okta's deprovisioning shape; without parsing it a "remove member" is silently ignored.
    [GeneratedRegex("^value\\s+eq\\s+\"(?<id>[^\"]*)\"$", RegexOptions.IgnoreCase)]
    private static partial Regex MemberValueFilter();

    /// <summary>
    /// Applies user PATCH operations. Returns the operations that could NOT be honoured, so the caller can
    /// answer 400 instead of 200.
    /// </summary>
    /// <remarks>
    /// <c>remove</c> was not handled at all — it fell through the <c>replace or add</c> test and was silently
    /// discarded — and an unrecognised path did nothing while the response still said 200. The
    /// ServiceProviderConfig advertises <c>patch.supported = true</c>, so a client had every reason to
    /// believe a clear-attribute or unknown-path operation had been applied when it had not. Silent success
    /// on a write is the failure mode that matters: a directory believes it deprovisioned an attribute and
    /// never retries.
    /// </remarks>
    public static IReadOnlyList<string> ApplyToUser(AuthUser user, IReadOnlyList<PatchOperation> operations)
    {
        var unsupported = new List<string>();

        foreach (var op in operations)
        {
            var normalizedOp = op.Op.ToLowerInvariant();
            var path = NormalizePath(op.Path);

            switch (normalizedOp)
            {
                case "replace" or "add":
                    if (op.Value is null)
                    {
                        unsupported.Add($"{op.Op} {op.Path}: no value supplied");
                        continue;
                    }
                    if (!ApplyUserValue(user, path, op.Value.Value))
                        unsupported.Add($"{op.Op} {op.Path}: unsupported path");
                    break;

                case "remove":
                    if (!RemoveUserValue(user, path))
                        unsupported.Add($"remove {op.Path}: unsupported path");
                    break;

                default:
                    unsupported.Add($"{op.Op}: unsupported operation");
                    break;
            }
        }

        return unsupported;
    }

    /// <summary>
    /// Clears a user attribute. Only attributes with a meaningful empty state are removable: RFC 7644
    /// §3.5.2.2 aside, clearing <c>userName</c> or <c>active</c> would leave the resource unusable, so those
    /// are refused rather than silently ignored.
    /// </summary>
    private static bool RemoveUserValue(AuthUser user, string? path)
    {
        switch (path?.ToLowerInvariant())
        {
            case "name.givenname":
                user.FirstName = null;
                return true;
            case "name.familyname":
                user.LastName = null;
                return true;
            case "displayname":
                user.FirstName = null;
                user.LastName = null;
                return true;
            case "externalid":
                user.ExternalId = null;
                return true;
            case "preferredlanguage" or "locale":
                user.Locale = null;
                return true;
            default:
                return false;
        }
    }

    public static void ApplyToGroup(ScimGroup group, IReadOnlyList<PatchOperation> operations)
    {
        foreach (var op in operations)
        {
            var normalizedOp = op.Op.ToLowerInvariant();
            var path = NormalizePath(op.Path);

            switch (normalizedOp)
            {
                case "add" when IsMembersPath(path) && op.Value is not null:
                    AddGroupMembers(group, op.Value.Value);
                    break;

                case "replace" when IsMembersPath(path):
                    // Full membership replacement ("set members"): drop the current set, add the supplied
                    // one. Previously this fell through to ApplyGroupValue, which handles only
                    // displayName/externalId — so a replace-members PATCH was silently dropped.
                    group.MemberUserIds.Clear();
                    if (op.Value is not null)
                        AddGroupMembers(group, op.Value.Value);
                    break;

                case "replace" or "add" when op.Value is not null:
                    ApplyGroupValue(group, path, op.Value.Value);
                    break;

                case "remove":
                    // Three member-removal shapes must all deprovision (or a removed user keeps mapped
                    // roles at next token issuance):
                    //   (1) path = members[value eq "id"]   → Okta: id encoded in the path filter (no value)
                    //   (2) path = members, value=[{value}] → Entra: id(s) in the value array
                    //   (3) path = members, no value        → remove ALL members
                    var filterId = ExtractMemberIdFromPath(path);
                    if (filterId is not null)
                    {
                        group.MemberUserIds.Remove(filterId);
                    }
                    else if (IsMembersPath(path))
                    {
                        if (op.Value is not null)
                            RemoveGroupMembers(group, op.Value.Value);
                        else
                            group.MemberUserIds.Clear();
                    }
                    break;
            }
        }
    }

    private static bool ApplyUserValue(AuthUser user, string? path, JsonElement value)
    {
        switch (path?.ToLowerInvariant())
        {
            case "username" or "emails" or "emails[type eq \"work\"].value":
                var email = ExtractStringOrEmail(value);
                if (!string.IsNullOrEmpty(email))
                {
                    user.Email = email;
                    user.NormalizedEmail = email.ToUpperInvariant();
                }
                break;
            case "name.givenname":
                user.FirstName = value.GetString();
                break;
            case "name.familyname":
                user.LastName = value.GetString();
                break;
            case "displayname":
                // Parse display name into first/last
                var display = value.GetString();
                if (!string.IsNullOrEmpty(display))
                {
                    var parts = display.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    user.FirstName = parts.Length > 0 ? parts[0] : null;
                    user.LastName = parts.Length > 1 ? parts[1] : null;
                }
                break;
            case "active":
                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    user.IsActive = value.GetBoolean();
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    // An unparseable value used to evaluate to false, so PATCH active="maybe" — or any
                    // typo — silently DEPROVISIONED the user. Refused instead: the operation says
                    // nothing intelligible, and guessing the destructive reading is the worst choice.
                    if (!bool.TryParse(value.GetString(), out var b))
                        throw new ScimPatchException("invalidValue", "active must be a boolean.");
                    user.IsActive = b;
                }
                break;
            case "externalid":
                user.ExternalId = value.GetString();
                break;
            case "preferredlanguage" or "locale":
                user.Locale = Locales.Normalize(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
                break;
            case null or "":
                // Value might be the full resource — apply individual fields
                if (value.ValueKind == JsonValueKind.Object)
                {
                    ApplyUserFromObject(user, value);
                    return true;
                }
                return false;
            default:
                return false;
        }

        return true;
    }

    private static void ApplyUserFromObject(AuthUser user, JsonElement obj)
    {
        if (obj.TryGetProperty("active", out var active))
        {
            if (active.ValueKind == JsonValueKind.True || active.ValueKind == JsonValueKind.False)
                user.IsActive = active.GetBoolean();
            else if (active.ValueKind == JsonValueKind.String)
            {
                // Same rule as the path-addressed form above, so the two shapes agree.
                if (!bool.TryParse(active.GetString(), out var activeBool))
                    throw new ScimPatchException("invalidValue", "active must be a boolean.");
                user.IsActive = activeBool;
            }
        }

        if (obj.TryGetProperty("userName", out var userName) && userName.ValueKind == JsonValueKind.String)
        {
            var email = userName.GetString();
            if (!string.IsNullOrEmpty(email))
            {
                user.Email = email;
                user.NormalizedEmail = email.ToUpperInvariant();
            }
        }

        if (obj.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("givenName", out var gn) && gn.ValueKind == JsonValueKind.String)
                user.FirstName = gn.GetString();
            if (name.TryGetProperty("familyName", out var fn) && fn.ValueKind == JsonValueKind.String)
                user.LastName = fn.GetString();
        }

        if (obj.TryGetProperty("externalId", out var extId) && extId.ValueKind == JsonValueKind.String)
            user.ExternalId = extId.GetString();

        // Prefer preferredLanguage; fall back to locale when only that is sent.
        if (obj.TryGetProperty("preferredLanguage", out var prefLang) && prefLang.ValueKind == JsonValueKind.String)
            user.Locale = Locales.Normalize(prefLang.GetString());
        else if (obj.TryGetProperty("locale", out var loc) && loc.ValueKind == JsonValueKind.String)
            user.Locale = Locales.Normalize(loc.GetString());
    }

    private static void ApplyGroupValue(ScimGroup group, string? path, JsonElement value)
    {
        switch (path?.ToLowerInvariant())
        {
            case "displayname":
                group.DisplayName = value.GetString() ?? group.DisplayName;
                break;
            case "externalid":
                group.ExternalId = value.GetString();
                break;
        }
    }

    private static bool IsMembersPath(string? path)
        => string.Equals(path, "members", StringComparison.OrdinalIgnoreCase);

    // Parses a member value-path filter (members[value eq "id"]) and returns the id, or null when the
    // path isn't that shape.
    private static string? ExtractMemberIdFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var open = path.IndexOf('[');
        if (open < 0 || !path.EndsWith(']'))
            return null;

        var attr = path[..open].Trim();
        if (!string.Equals(attr, "members", StringComparison.OrdinalIgnoreCase))
            return null;

        var filter = path[(open + 1)..^1].Trim();
        var match = MemberValueFilter().Match(filter);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static void AddGroupMembers(ScimGroup group, JsonElement value)
    {
        var memberIds = ExtractMemberIds(value);
        foreach (var id in memberIds)
        {
            if (!group.MemberUserIds.Contains(id))
                group.MemberUserIds.Add(id);
        }
    }

    private static void RemoveGroupMembers(ScimGroup group, JsonElement value)
    {
        var memberIds = ExtractMemberIds(value);
        foreach (var id in memberIds)
        {
            group.MemberUserIds.Remove(id);
        }
    }

    private static List<string> ExtractMemberIds(JsonElement value)
    {
        var ids = new List<string>();

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    ids.Add(v.GetString()!);
            }
        }

        return ids;
    }

    /// <summary>
    /// The address a PATCH on <c>emails</c> means, which is the PRIMARY one.
    /// </summary>
    /// <remarks>
    /// This took the first element of the array. `emails` is multi-valued and unordered, and POST and
    /// PUT both select on <c>primary</c> — so an IdP sending [work-alias, primary] in that order (the
    /// order is theirs, not the spec's) rewrote the account's userName, its normalized email and its
    /// login identity to an ALIAS. The account then answered to an address its owner may not control,
    /// and the real one stopped resolving.
    /// </remarks>
    private static string? ExtractStringOrEmail(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Array)
        {
            string? firstAny = null;

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;

                if (item.TryGetProperty("primary", out var primary)
                    && primary.ValueKind == JsonValueKind.True)
                {
                    return v.GetString();
                }

                firstAny ??= v.GetString();
            }

            // No element claimed primary: fall back to the first, matching what POST and PUT do when
            // the provisioning client marks nothing.
            return firstAny;
        }

        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Remove "urn:ietf:params:scim:schemas:core:2.0:User:" prefix if present
        const string userSchemaPrefix = "urn:ietf:params:scim:schemas:core:2.0:User:";
        if (path.StartsWith(userSchemaPrefix, StringComparison.OrdinalIgnoreCase))
            path = path[userSchemaPrefix.Length..];

        const string groupSchemaPrefix = "urn:ietf:params:scim:schemas:core:2.0:Group:";
        if (path.StartsWith(groupSchemaPrefix, StringComparison.OrdinalIgnoreCase))
            path = path[groupSchemaPrefix.Length..];

        return path;
    }
}
