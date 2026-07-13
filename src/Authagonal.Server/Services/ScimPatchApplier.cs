using System.Text.Json;
using System.Text.RegularExpressions;
using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

public static partial class ScimPatchApplier
{
    public sealed record PatchOperation(string Op, string? Path, JsonElement? Value);

    // SCIM value-path filter carrying the member id in the PATH, e.g. members[value eq "abc-123"].
    // This is Okta's deprovisioning shape; without parsing it a "remove member" is silently ignored.
    [GeneratedRegex("^value\\s+eq\\s+\"(?<id>[^\"]*)\"$", RegexOptions.IgnoreCase)]
    private static partial Regex MemberValueFilter();

    public static void ApplyToUser(AuthUser user, IReadOnlyList<PatchOperation> operations)
    {
        foreach (var op in operations)
        {
            var normalizedOp = op.Op.ToLowerInvariant();
            var path = NormalizePath(op.Path);

            if (normalizedOp is "replace" or "add")
            {
                if (op.Value is null)
                    continue;

                ApplyUserValue(user, path, op.Value.Value);
            }
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

    private static void ApplyUserValue(AuthUser user, string? path, JsonElement value)
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
                    user.IsActive = bool.TryParse(value.GetString(), out var b) && b;
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
                }
                break;
        }
    }

    private static void ApplyUserFromObject(AuthUser user, JsonElement obj)
    {
        if (obj.TryGetProperty("active", out var active))
        {
            if (active.ValueKind == JsonValueKind.True || active.ValueKind == JsonValueKind.False)
                user.IsActive = active.GetBoolean();
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

    private static string? ExtractStringOrEmail(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
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
