using System.Text.Json.Serialization;
using Authagonal.Core.Models;

namespace Authagonal.Server.Endpoints.Scim;

public sealed class ScimUserResource
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; set; } = ["urn:ietf:params:scim:schemas:core:2.0:User"];

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    [JsonPropertyName("userName")]
    public required string UserName { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimName? Name { get; set; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("emails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimEmail[]? Emails { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("preferredLanguage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferredLanguage { get; set; }

    [JsonPropertyName("meta")]
    public required ScimMeta Meta { get; set; }

    public static ScimUserResource FromUser(AuthUser user, string baseUrl)
    {
        var displayName = $"{user.FirstName} {user.LastName}".Trim();
        return new ScimUserResource
        {
            Id = user.Id,
            ExternalId = user.ExternalId,
            UserName = user.Email,
            PreferredLanguage = user.Locale,
            Name = new ScimName
            {
                GivenName = user.FirstName,
                FamilyName = user.LastName,
                Formatted = string.IsNullOrWhiteSpace(displayName) ? user.Email : displayName,
            },
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Email : displayName,
            Emails =
            [
                new ScimEmail { Value = user.Email, Primary = true, Type = "work" }
            ],
            Active = user.IsActive,
            Meta = new ScimMeta
            {
                ResourceType = "User",
                Created = user.CreatedAt,
                LastModified = user.UpdatedAt ?? user.CreatedAt,
                Location = $"{baseUrl}/scim/v2/Users/{user.Id}",
            },
        };
    }
}

public sealed class ScimName
{
    [JsonPropertyName("formatted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Formatted { get; set; }

    [JsonPropertyName("familyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FamilyName { get; set; }

    [JsonPropertyName("givenName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GivenName { get; set; }
}

public sealed class ScimEmail
{
    [JsonPropertyName("value")]
    public required string Value { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "work";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

public sealed class ScimGroupResource
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; set; } = ["urn:ietf:params:scim:schemas:core:2.0:Group"];

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; set; }

    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScimMember[]? Members { get; set; }

    [JsonPropertyName("meta")]
    public required ScimMeta Meta { get; set; }

    public static ScimGroupResource FromGroup(ScimGroup group, string baseUrl)
    {
        return new ScimGroupResource
        {
            Id = group.Id,
            ExternalId = group.ExternalId,
            DisplayName = group.DisplayName,
            Members = group.MemberUserIds.Select(uid => new ScimMember
            {
                Value = uid,
                Ref = $"{baseUrl}/scim/v2/Users/{uid}",
                Type = "User",
            }).ToArray(),
            Meta = new ScimMeta
            {
                ResourceType = "Group",
                Created = group.CreatedAt,
                LastModified = group.UpdatedAt ?? group.CreatedAt,
                Location = $"{baseUrl}/scim/v2/Groups/{group.Id}",
            },
        };
    }
}

public sealed class ScimMember
{
    [JsonPropertyName("value")]
    public required string Value { get; set; }

    [JsonPropertyName("$ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
}

public sealed class ScimMeta
{
    [JsonPropertyName("resourceType")]
    public required string ResourceType { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset LastModified { get; set; }

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; set; }
}

public sealed class ScimListResponse<T>
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; set; } = ["urn:ietf:params:scim:api:messages:2.0:ListResponse"];

    /// <summary>
    /// Total matching resources, OMITTED when the listing is incomplete.
    /// </summary>
    /// <remarks>
    /// Under cursor pagination the true total is unknowable without a full scan, and this used to report the
    /// returned page's size instead. A syncing client reads <c>totalResults</c>, sees it equal the number of
    /// resources it just received, and concludes it has the whole directory — silently missing every user
    /// beyond the first page, which for a deprovisioning sync means offboarded accounts are never removed.
    /// draft-ietf-scim-cursor-pagination permits omitting it when unknown; a client that then errors is
    /// failing loudly, which is strictly better than silently under-reading the directory.
    /// </remarks>
    [JsonPropertyName("totalResults")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalResults { get; set; }

    [JsonPropertyName("startIndex")]
    public int StartIndex { get; set; }

    [JsonPropertyName("itemsPerPage")]
    public int ItemsPerPage { get; set; }

    /// <summary>Cursor pagination (draft-ietf-scim-cursor-pagination): pass back as <c>?cursor=</c>
    /// for the next page. Absent when the listing is exhausted.</summary>
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }

    [JsonPropertyName("Resources")]
    public required IReadOnlyList<T> Resources { get; set; }
}

public sealed class ScimPatchRequest
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; set; } = ["urn:ietf:params:scim:api:messages:2.0:PatchOp"];

    [JsonPropertyName("Operations")]
    public ScimPatchOperation[] Operations { get; set; } = [];
}

public sealed class ScimPatchOperation
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("value")]
    public System.Text.Json.JsonElement? Value { get; set; }
}

public sealed class ScimCreateUserRequest
{
    [JsonPropertyName("schemas")]
    public string[]? Schemas { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("name")]
    public ScimName? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("emails")]
    public ScimEmail[]? Emails { get; set; }

    /// <summary>
    /// NULLABLE so "omitted" is distinguishable from "false".
    /// </summary>
    /// <remarks>
    /// This was a non-nullable <c>bool</c> defaulting to <c>true</c>, and <c>PUT /Users/{id}</c> assigned it
    /// straight onto <c>IsActive</c> — so a PUT that simply did not mention <c>active</c> REACTIVATED a
    /// deprovisioned user. An offboarded account came back on the next directory sync, silently. On create,
    /// omitted still means active (a new user is active by default); on replace, omitted now means unchanged.
    /// </remarks>
    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    /// <summary>Create semantics: a new user is active unless explicitly told otherwise.</summary>
    public bool ActiveOnCreate => Active ?? true;

    /// <summary>SCIM core attribute (RFC 7643) for the user's preferred written/spoken language —
    /// the IdP's directory value. Mapped to <c>AuthUser.Locale</c> so SSO/SCIM users (who never see a
    /// registration page) still get localised emails. We prefer this over <c>locale</c> (which is
    /// about formatting); accept either via <see cref="PreferredLanguageOrLocale"/>.</summary>
    [JsonPropertyName("preferredLanguage")]
    public string? PreferredLanguage { get; set; }

    /// <summary>SCIM core <c>locale</c> attribute — formatting locale; used as a fallback when an IdP
    /// sends only <c>locale</c> and no <c>preferredLanguage</c>.</summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    /// <summary>The user-language signal to store, preferring <c>preferredLanguage</c> over <c>locale</c>.</summary>
    [JsonIgnore]
    public string? PreferredLanguageOrLocale =>
        !string.IsNullOrWhiteSpace(PreferredLanguage) ? PreferredLanguage : Locale;
}

public sealed class ScimCreateGroupRequest
{
    [JsonPropertyName("schemas")]
    public string[]? Schemas { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("members")]
    public ScimMember[]? Members { get; set; }
}
