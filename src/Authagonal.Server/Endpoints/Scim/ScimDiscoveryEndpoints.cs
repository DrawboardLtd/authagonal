namespace Authagonal.Server.Endpoints.Scim;

public static class ScimDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapScimDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        // Entra ID uses /scim/ prefix; also support /scim/v2/ for direct use
        app.MapGet("/scim/", GetServiceProviderConfig).AllowAnonymous();
        app.MapGet("/scim/v2/", GetServiceProviderConfig).AllowAnonymous();

        app.MapGet("/scim/ServiceProviderConfig", GetServiceProviderConfig).AllowAnonymous();
        app.MapGet("/scim/v2/ServiceProviderConfig", GetServiceProviderConfig).AllowAnonymous();
        app.MapGet("/scim/Schemas", GetSchemas).AllowAnonymous();
        app.MapGet("/scim/v2/Schemas", GetSchemas).AllowAnonymous();
        app.MapGet("/scim/ResourceTypes", GetResourceTypes).AllowAnonymous();
        app.MapGet("/scim/v2/ResourceTypes", GetResourceTypes).AllowAnonymous();

        // RFC 7644 §4 defines these as addressable resources, and every one of them already
        // advertised its own meta.location — at a URL nothing served. The requests fell through the
        // API routes to the SPA fallback, so a client that followed the location it was given got
        // 200 text/html: the login page, parsed as a SCIM resource. A discovery client cannot tell
        // that from a real answer, and "the schema endpoint returned HTML" is a confusing enough
        // symptom that it reads as a routing bug rather than a missing route.
        app.MapGet("/scim/Schemas/{id}", GetSchemaById).AllowAnonymous();
        app.MapGet("/scim/v2/Schemas/{id}", GetSchemaById).AllowAnonymous();
        app.MapGet("/scim/ResourceTypes/{id}", GetResourceTypeById).AllowAnonymous();
        app.MapGet("/scim/v2/ResourceTypes/{id}", GetResourceTypeById).AllowAnonymous();

        return app;
    }

    /// <summary>RFC 7644 §4: a single schema, addressed by its URN.</summary>
    private static IResult GetSchemaById(string id, Authagonal.Core.Services.ITenantContext tenantContext)
    {
        var match = SchemaResources(tenantContext.Issuer)
            .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        return match is null
            ? ScimResults.NotFound($"Schema '{id}' not found")
            : ScimResults.SuccessVerbatim(match.Body);
    }

    /// <summary>RFC 7644 §4: a single resource type, addressed by its id ("User" / "Group").</summary>
    private static IResult GetResourceTypeById(string id, Authagonal.Core.Services.ITenantContext tenantContext)
    {
        var match = ResourceTypeResources(tenantContext.Issuer)
            .FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? ScimResults.NotFound($"ResourceType '{id}' not found")
            : ScimResults.SuccessVerbatim(match.Body);
    }

    /// <summary>An addressable discovery resource: its identifier, and the body both the collection
    /// and the single-resource route serve.</summary>
    private sealed record DiscoveryResource(string Id, object Body);

    private static IResult GetServiceProviderConfig(Authagonal.Core.Services.ITenantContext tenantContext)
    {
        var baseUrl = tenantContext.Issuer;

        var config = new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            documentationUri = $"{baseUrl}/docs/scim",
            patch = new { supported = true },
            // Pagination is CURSOR-based (draft-ietf-scim-cursor-pagination): pass a response's
            // `nextCursor` back as ?cursor=. `startIndex` past the first page is refused with 400, and
            // nothing here said so — a conforming client had to discover that by failing. `totalResults` is
            // omitted while a listing is incomplete, because under cursor pagination the true total is not
            // knowable without a full scan and reporting the page size instead led syncing clients to stop
            // after one page and silently miss users.
            // `index = true` because the provider DOES support it — /Groups pages by startIndex and only
            // by startIndex. This said false, which is a per-provider claim (draft-ietf-scim-cursor-pagination
            // §4 has no per-endpoint qualifier), and it was false about the Groups collection: an integrator
            // building against this advertisement could never read past the first page of groups, because the
            // model it was told to use did not exist there and the one that worked was declared unsupported.
            //
            // /Groups now also accepts `cursor` and returns `nextCursor`, so a cursor-only client works
            // against both collections; the token is opaque, which is the whole point of a cursor, so it
            // carrying an index is between the server and itself.
            pagination = new
            {
                cursor = true,
                index = true,
                defaultPageSize = 100,
                maxPageSize = 200,
            },
            bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
            filter = new { supported = true, maxResults = 200 },
            changePassword = new { supported = false },
            sort = new { supported = false },
            etag = new { supported = false },
            authenticationSchemes = new[]
            {
                new
                {
                    type = "oauthbearertoken",
                    name = "OAuth Bearer Token",
                    description = "Authentication scheme using a static Bearer token per SCIM client.",
                }
            },
            meta = new
            {
                resourceType = "ServiceProviderConfig",
                location = $"{baseUrl}/scim/v2/ServiceProviderConfig",
            },
        };

        return ScimResults.Success(config);
    }

    private static IResult GetSchemas(Authagonal.Core.Services.ITenantContext tenantContext)
    {
        var schemas = SchemaResources(tenantContext.Issuer).Select(s => s.Body).ToArray();

        var response = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = schemas.Length,
            Resources = schemas,
        };

        return ScimResults.SuccessVerbatim(response);
    }

    private static IResult GetResourceTypes(Authagonal.Core.Services.ITenantContext tenantContext)
    {
        var resourceTypes = ResourceTypeResources(tenantContext.Issuer).Select(r => r.Body).ToArray();

        var response = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = resourceTypes.Length,
            Resources = resourceTypes,
        };

        return ScimResults.SuccessVerbatim(response);
    }

    /// <summary>
    /// The one definition both the collection and the single-resource route serve, so the body a
    /// client gets from meta.location cannot drift from the body it got in the listing.
    /// </summary>
    private static DiscoveryResource[] SchemaResources(string baseUrl) =>
    [
        new("urn:ietf:params:scim:schemas:core:2.0:User", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Schema" },
            id = "urn:ietf:params:scim:schemas:core:2.0:User",
            name = "User",
            description = "User Account",
            attributes = new object[]
            {
                // id / meta are RFC 7643 §3.1 common attributes. They were omitted here while being
                // returned on every User, which is the same defect as omitting preferredLanguage:
                // an integrator building a mapping from /Schemas cannot see them.
                SchemaAttribute("id", "string", "Server-assigned unique identifier for the User.",
                    required: true, mutability: "readOnly", returned: "always", uniqueness: "server"),
                SchemaAttribute("externalId", "string", "External identifier from the provisioning client."),
                SchemaAttribute("userName", "string", "Unique identifier for the User, typically email.", required: true, uniqueness: "server"),
                SchemaAttribute("name", "complex", "The components of the user's real name.", subAttributes: new object[]
                {
                    SchemaAttribute("givenName", "string", "The given name of the User."),
                    SchemaAttribute("familyName", "string", "The family name of the User."),
                    SchemaAttribute("formatted", "string", "The full name."),
                }),
                SchemaAttribute("displayName", "string", "The name of the User suitable for display."),
                SchemaAttribute("emails", "complex", "Email addresses for the User.", multiValued: true, subAttributes: new object[]
                {
                    SchemaAttribute("value", "string", "Email address value."),
                    SchemaAttribute("type", "string", "Email type (e.g. work)."),
                    SchemaAttribute("primary", "boolean", "Is this the primary email."),
                }),
                SchemaAttribute("active", "boolean", "Whether the user account is active."),
                SchemaAttribute("preferredLanguage", "string",
                    "The User's preferred written or spoken language, used to localise the emails Authagonal sends. " +
                    "Stored as the user's locale."),
                // locale is accepted on write purely as a fallback for IdPs that send it and no
                // preferredLanguage; both land in the same stored field and the User resource
                // returns it as preferredLanguage. returned="never" (RFC 7643 §7: "the value is not
                // stored") is the accurate way to say write-only-alias, and is what keeps the
                // round-trip test from demanding a locale member that will never appear.
                SchemaAttribute("locale", "string",
                    "Formatting locale. Accepted on create/replace as a fallback when the client sends no " +
                    "preferredLanguage; the stored value is returned as preferredLanguage, never as locale.",
                    returned: "never"),
                MetaAttribute("User"),
            },
            meta = new { resourceType = "Schema", location = $"{baseUrl}/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:User" },
        }),
        new("urn:ietf:params:scim:schemas:core:2.0:Group", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Schema" },
            id = "urn:ietf:params:scim:schemas:core:2.0:Group",
            name = "Group",
            description = "Group",
            attributes = new object[]
            {
                SchemaAttribute("id", "string", "Server-assigned unique identifier for the Group.",
                    required: true, mutability: "readOnly", returned: "always", uniqueness: "server"),
                SchemaAttribute("externalId", "string", "External identifier from the provisioning client."),
                SchemaAttribute("displayName", "string", "A human-readable name for the Group.", required: true),
                SchemaAttribute("members", "complex", "A list of members of the Group.", multiValued: true, subAttributes: new object[]
                {
                    SchemaAttribute("value", "string", "Identifier of the group member."),
                    // §7 requires referenceTypes on a reference-typed attribute. Without it a client
                    // has a URI and no statement of what it points at; Groups only ever hold Users.
                    SchemaAttribute("$ref", "reference", "The URI of the member resource.", referenceTypes: ["User"]),
                    SchemaAttribute("type", "string", "The type of the member (User)."),
                }),
                MetaAttribute("Group"),
            },
            meta = new { resourceType = "Schema", location = $"{baseUrl}/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:Group" },
        }),
    ];

    private static DiscoveryResource[] ResourceTypeResources(string baseUrl) =>
    [
        new("User", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
            id = "User",
            name = "User",
            endpoint = "/scim/v2/Users",
            description = "User Account",
            schema = "urn:ietf:params:scim:schemas:core:2.0:User",
            meta = new { resourceType = "ResourceType", location = $"{baseUrl}/scim/v2/ResourceTypes/User" },
        }),
        new("Group", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
            id = "Group",
            name = "Group",
            endpoint = "/scim/v2/Groups",
            description = "Group",
            schema = "urn:ietf:params:scim:schemas:core:2.0:Group",
            meta = new { resourceType = "ResourceType", location = $"{baseUrl}/scim/v2/ResourceTypes/Group" },
        }),
    ];

    /// <summary>
    /// RFC 7643 §3.1 <c>meta</c>, identical on every resource type bar the resourceType description.
    /// </summary>
    private static Dictionary<string, object> MetaAttribute(string resourceType) =>
        SchemaAttribute("meta", "complex", "Resource metadata.", mutability: "readOnly", subAttributes:
        [
            SchemaAttribute("resourceType", "string", $"The name of the resource type — \"{resourceType}\".", mutability: "readOnly"),
            SchemaAttribute("created", "dateTime", "When the resource was added to the service provider.", mutability: "readOnly"),
            SchemaAttribute("lastModified", "dateTime", "The most recent modification to the resource.", mutability: "readOnly"),
            SchemaAttribute("location", "reference", "The URI of the resource.", mutability: "readOnly", referenceTypes: ["uri"]),
        ]);

    /// <summary>
    /// One attribute definition per RFC 7643 §7's schema-of-schemas.
    /// <para>
    /// Three things here are deliberate, each having been wrong before. <c>caseExact</c> is emitted for
    /// every attribute whose type has character comparison semantics (string, reference) and omitted for
    /// the rest, matching §8.7.1 — it is what tells a client whether <c>userName eq</c> is
    /// case-sensitive, and a client that finds nothing has to guess. The value is <c>false</c>
    /// throughout because that is what <see cref="Services.Scim.ScimFilterEvaluator"/> actually does: it
    /// compares every string with <c>OrdinalIgnoreCase</c>. If per-attribute case sensitivity is ever
    /// implemented there, these move with it, or the document goes back to lying.
    /// </para>
    /// <para>
    /// <c>referenceTypes</c> is required by §7 on a reference-typed attribute and has no sensible
    /// default, so it is passed explicitly. And <c>subAttributes</c> is omitted rather than emitted as
    /// <c>null</c>, which is what a dictionary buys over an anonymous type — a simple attribute has no
    /// sub-attributes, which is not the same statement as having a null list of them.
    /// </para>
    /// </summary>
    private static Dictionary<string, object> SchemaAttribute(
        string name, string type, string description,
        bool required = false, bool multiValued = false,
        string mutability = "readWrite", string returned = "default",
        string uniqueness = "none",
        string[]? referenceTypes = null, object[]? subAttributes = null)
    {
        var attribute = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = type,
            ["description"] = description,
            ["required"] = required,
            ["multiValued"] = multiValued,
            ["mutability"] = mutability,
            ["returned"] = returned,
            ["uniqueness"] = uniqueness,
        };

        if (type is "string" or "reference")
            attribute["caseExact"] = false;
        if (referenceTypes is not null)
            attribute["referenceTypes"] = referenceTypes;
        if (subAttributes is not null)
            attribute["subAttributes"] = subAttributes;

        return attribute;
    }
}
