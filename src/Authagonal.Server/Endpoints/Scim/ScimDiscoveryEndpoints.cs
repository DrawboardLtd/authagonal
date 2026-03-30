namespace Authagonal.Server.Endpoints.Scim;

public static class ScimDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapScimDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        // Base URL handler — Entra ID hits this during SCIM credential validation
        app.MapGet("/scim/v2/", GetServiceProviderConfig).AllowAnonymous();

        app.MapGet("/scim/v2/ServiceProviderConfig", GetServiceProviderConfig).AllowAnonymous();
        app.MapGet("/scim/v2/Schemas", GetSchemas).AllowAnonymous();
        app.MapGet("/scim/v2/ResourceTypes", GetResourceTypes).AllowAnonymous();

        return app;
    }

    private static IResult GetServiceProviderConfig(IConfiguration configuration)
    {
        var baseUrl = configuration["Issuer"] ?? "https://localhost";

        var config = new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            documentationUri = $"{baseUrl}/docs/scim",
            patch = new { supported = true },
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

    private static IResult GetSchemas(IConfiguration configuration)
    {
        var baseUrl = configuration["Issuer"] ?? "https://localhost";

        var schemas = new object[]
        {
            new
            {
                id = "urn:ietf:params:scim:schemas:core:2.0:User",
                name = "User",
                description = "User Account",
                attributes = new object[]
                {
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
                    SchemaAttribute("externalId", "string", "External identifier from the provisioning client."),
                },
                meta = new { resourceType = "Schema", location = $"{baseUrl}/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:User" },
            },
            new
            {
                id = "urn:ietf:params:scim:schemas:core:2.0:Group",
                name = "Group",
                description = "Group",
                attributes = new object[]
                {
                    SchemaAttribute("displayName", "string", "A human-readable name for the Group.", required: true),
                    SchemaAttribute("members", "complex", "A list of members of the Group.", multiValued: true, subAttributes: new object[]
                    {
                        SchemaAttribute("value", "string", "Identifier of the group member."),
                        SchemaAttribute("$ref", "reference", "The URI of the member resource."),
                        SchemaAttribute("type", "string", "The type of the member (User)."),
                    }),
                    SchemaAttribute("externalId", "string", "External identifier from the provisioning client."),
                },
                meta = new { resourceType = "Schema", location = $"{baseUrl}/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:Group" },
            },
        };

        var response = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = schemas.Length,
            Resources = schemas,
        };

        return ScimResults.Success(response);
    }

    private static IResult GetResourceTypes(IConfiguration configuration)
    {
        var baseUrl = configuration["Issuer"] ?? "https://localhost";

        var resourceTypes = new object[]
        {
            new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "User",
                name = "User",
                endpoint = "/scim/v2/Users",
                description = "User Account",
                schema = "urn:ietf:params:scim:schemas:core:2.0:User",
                meta = new { resourceType = "ResourceType", location = $"{baseUrl}/scim/v2/ResourceTypes/User" },
            },
            new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                id = "Group",
                name = "Group",
                endpoint = "/scim/v2/Groups",
                description = "Group",
                schema = "urn:ietf:params:scim:schemas:core:2.0:Group",
                meta = new { resourceType = "ResourceType", location = $"{baseUrl}/scim/v2/ResourceTypes/Group" },
            },
        };

        var response = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = resourceTypes.Length,
            Resources = resourceTypes,
        };

        return ScimResults.Success(response);
    }

    private static object SchemaAttribute(
        string name, string type, string description,
        bool required = false, bool multiValued = false,
        string? uniqueness = null, object[]? subAttributes = null)
    {
        return new
        {
            name,
            type,
            description,
            required,
            multiValued,
            mutability = "readWrite",
            returned = "default",
            uniqueness = uniqueness ?? "none",
            subAttributes,
        };
    }
}
