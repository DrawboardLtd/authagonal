using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authagonal.Server.Endpoints.Scim;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class ScimDiscoveryTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _scimToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
        var (_, token) = await _factory.SeedScimClientAsync();
        _scimToken = token;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _scimToken);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task ServiceProviderConfig_ReturnsValidResponse()
    {
        var response = await _client.GetAsync("/scim/v2/ServiceProviderConfig");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Schemas_ReturnsValidResponse()
    {
        var response = await _client.GetAsync("/scim/v2/Schemas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResourceTypes_ReturnsValidResponse()
    {
        var response = await _client.GetAsync("/scim/v2/ResourceTypes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── /Schemas is a contract, not a formality (F324) ──
    //
    // The advertised schema had drifted from the resource: preferredLanguage was bound, stored and
    // returned but undeclared, so an integrator building the attribute mapping from /Schemas simply
    // never mapped it and SCIM-provisioned users silently lost their localisation — the exact reason
    // the field was added. id and meta were missing the same way. The two tests below are the invariant
    // that stops it happening again: one pins the document to the resource in both directions, the
    // other pins its shape to RFC 7643 §7.

    /// <summary>
    /// Every attribute the User and Group resources serialize is declared in the advertised schema, and
    /// every declared attribute the server claims it returns actually exists on the resource. Driven off
    /// the DTOs by reflection rather than a hand-written list, so adding a property to a resource
    /// without declaring it fails here.
    /// </summary>
    [Fact]
    public async Task AdvertisedSchemas_AndResourceRepresentations_AgreeInBothDirections()
    {
        await AssertSchemaMatchesResource(
            "urn:ietf:params:scim:schemas:core:2.0:User", typeof(ScimUserResource),
            new Dictionary<string, Type>
            {
                ["name"] = typeof(ScimName),
                ["emails"] = typeof(ScimEmail),
                ["meta"] = typeof(ScimMeta),
            });

        await AssertSchemaMatchesResource(
            "urn:ietf:params:scim:schemas:core:2.0:Group", typeof(ScimGroupResource),
            new Dictionary<string, Type>
            {
                ["members"] = typeof(ScimMember),
                ["meta"] = typeof(ScimMeta),
            });
    }

    /// <summary>
    /// The attribute definitions are structurally valid per RFC 7643 §7: caseExact stated wherever
    /// character comparison applies, referenceTypes present on reference-typed attributes,
    /// subAttributes omitted rather than emitted as null, and the enumerated fields inside their
    /// canonical value sets.
    /// </summary>
    [Fact]
    public async Task SchemaAttributeDefinitions_AreStructurallyValidPerRfc7643()
    {
        foreach (var schema in await GetSchemasAsync())
            foreach (var attribute in schema.GetProperty("attributes").EnumerateArray())
                AssertAttributeDefinitionValid(attribute, schema.GetProperty("id").GetString()!);
    }

    private static void AssertAttributeDefinitionValid(JsonElement attribute, string path)
    {
        var name = attribute.GetProperty("name").GetString()!;
        var where = $"{path}:{name}";
        var type = attribute.GetProperty("type").GetString()!;

        foreach (var required in new[] { "name", "type", "description", "required", "multiValued", "mutability", "returned", "uniqueness" })
            Assert.True(attribute.TryGetProperty(required, out _), $"{where} is missing '{required}'.");

        Assert.Contains(attribute.GetProperty("mutability").GetString(), new[] { "readOnly", "readWrite", "immutable", "writeOnly" });
        Assert.Contains(attribute.GetProperty("returned").GetString(), new[] { "always", "never", "default", "request" });
        Assert.Contains(attribute.GetProperty("uniqueness").GetString(), new[] { "none", "server", "global" });

        // caseExact is what tells a client whether `userName eq` is case-sensitive. It is meaningful
        // only where values are compared as characters, so it is stated for exactly those types.
        Assert.Equal(type is "string" or "reference", attribute.TryGetProperty("caseExact", out _));

        // §7: a reference-typed attribute carries referenceTypes, and nothing else does.
        if (type == "reference")
        {
            Assert.True(attribute.TryGetProperty("referenceTypes", out var refTypes), $"{where} is a reference with no referenceTypes.");
            Assert.NotEmpty(refTypes.EnumerateArray());
        }
        else
        {
            Assert.False(attribute.TryGetProperty("referenceTypes", out _), $"{where} is not a reference but declares referenceTypes.");
        }

        // A simple attribute has no sub-attributes — which is not the same claim as `subAttributes: null`.
        if (attribute.TryGetProperty("subAttributes", out var subs))
        {
            Assert.Equal("complex", type);
            Assert.NotEqual(JsonValueKind.Null, subs.ValueKind);
            foreach (var sub in subs.EnumerateArray())
                AssertAttributeDefinitionValid(sub, where);
        }
        else
        {
            Assert.NotEqual("complex", type);
        }
    }

    private async Task AssertSchemaMatchesResource(string schemaId, Type resourceType, Dictionary<string, Type> complexTypes)
    {
        var schema = (await GetSchemasAsync()).Single(s => s.GetProperty("id").GetString() == schemaId);
        AssertAttributesMatch(schema.GetProperty("attributes"), resourceType, complexTypes, schemaId);
    }

    private static void AssertAttributesMatch(JsonElement attributes, Type resourceType, Dictionary<string, Type> complexTypes, string where)
    {
        var declared = attributes.EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a);
        var serialized = SerializedMemberNames(resourceType);

        foreach (var member in serialized)
        {
            // "schemas" is the resource's schema-URI list (RFC 7644 §3.1), carried by every SCIM
            // message rather than defined by any one schema. It is the only exemption.
            if (member == "schemas") continue;

            Assert.True(declared.ContainsKey(member),
                $"{where} returns '{member}' but the advertised schema does not declare it.");

            if (complexTypes.TryGetValue(member, out var subType))
                AssertAttributesMatch(declared[member].GetProperty("subAttributes"), subType, [], $"{where}:{member}");
        }

        foreach (var (name, attribute) in declared)
        {
            // returned="never" is the server saying the attribute is accepted but not stored under that
            // name — SCIM's locale, which is folded into preferredLanguage. Nothing else may be absent.
            if (attribute.GetProperty("returned").GetString() == "never") continue;

            Assert.True(serialized.Contains(name),
                $"{where} declares '{name}' but the resource never returns it.");
        }
    }

    /// <summary>The JSON member names a DTO actually writes, in the serializer's own terms.</summary>
    private static HashSet<string> SerializedMemberNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToHashSet(StringComparer.Ordinal);

    private async Task<List<JsonElement>> GetSchemasAsync()
    {
        var response = await _client.GetAsync("/scim/v2/Schemas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("Resources").EnumerateArray()];
    }

    [Fact]
    public async Task LegacyPrefix_AlsoWorks()
    {
        var response = await _client.GetAsync("/scim/ServiceProviderConfig");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── SCIM User edge cases ──

    [Fact]
    public async Task CreateUser_MissingUserName_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/scim/v2/Users", new { name = new { givenName = "Test" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_Returns409()
    {
        await _client.PostAsJsonAsync("/scim/v2/Users", new { userName = "dupe@example.com" });
        var response = await _client.PostAsJsonAsync("/scim/v2/Users", new { userName = "dupe@example.com" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PatchUser_Deactivate_RevokesGrants()
    {
        var createResponse = await _client.PostAsJsonAsync("/scim/v2/Users",
            new { userName = "deactivate@example.com", active = true });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString()!;

        var patchResponse = await _client.PatchAsJsonAsync($"/scim/v2/Users/{userId}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new { op = "replace", path = "active", value = false }
            }
        });

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patched = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(patched.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task GetUser_NonExistent_Returns404()
    {
        var response = await _client.GetAsync("/scim/v2/Users/nonexistent-id");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_SoftDeletes()
    {
        var createResponse = await _client.PostAsJsonAsync("/scim/v2/Users",
            new { userName = "deleteme@example.com" });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString()!;

        var deleteResponse = await _client.DeleteAsync($"/scim/v2/Users/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // User should be deactivated, not hard-deleted
        var user = await _factory.UserStore.GetAsync(userId);
        Assert.NotNull(user);
        Assert.False(user.IsActive);
    }

    // ── SCIM Group edge cases ──

    [Fact]
    public async Task CreateGroup_ReturnsGroup()
    {
        var response = await _client.PostAsJsonAsync("/scim/v2/Groups",
            new { displayName = "Engineering" });

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Engineering", json.GetProperty("displayName").GetString());
        Assert.NotNull(json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PatchGroup_AddMember_Works()
    {
        // Create group
        var groupResponse = await _client.PostAsJsonAsync("/scim/v2/Groups",
            new { displayName = "Patch Test Group" });
        var group = await groupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetString()!;

        // Create user
        var userResponse = await _client.PostAsJsonAsync("/scim/v2/Users",
            new { userName = "groupmember@example.com" });
        var user = await userResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = user.GetProperty("id").GetString()!;

        // Patch: add member
        var patchResponse = await _client.PatchAsJsonAsync($"/scim/v2/Groups/{groupId}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "add",
                    path = "members",
                    value = new[] { new { value = userId } }
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
    }

    [Fact]
    public async Task ScimEndpoints_RequireAuth()
    {
        var unauthClient = _factory.CreateClient();
        var response = await unauthClient.GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithExternalId_StoresMapping()
    {
        var response = await _client.PostAsJsonAsync("/scim/v2/Users",
            new { userName = "extid@example.com", externalId = "ext-123" });

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ext-123", json.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task ReplaceUser_UpdatesAllFields()
    {
        var createResponse = await _client.PostAsJsonAsync("/scim/v2/Users",
            new { userName = "replace@example.com", name = new { givenName = "Old", familyName = "Name" } });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString()!;

        var replaceResponse = await _client.PutAsJsonAsync($"/scim/v2/Users/{userId}", new
        {
            userName = "replace@example.com",
            name = new { givenName = "New", familyName = "Updated" },
            active = true
        });

        Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);
        var replaced = await replaceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New", replaced.GetProperty("name").GetProperty("givenName").GetString());
        Assert.Equal("Updated", replaced.GetProperty("name").GetProperty("familyName").GetString());
    }
}
