using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class ScimGroupEndpointTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    [Fact]
    public async Task CreateGroup_Returns201()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Engineering",
            externalId = "grp-eng",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Engineering", json.GetProperty("displayName").GetString());
        Assert.Equal("grp-eng", json.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task GetGroup_ReturnsGroup()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Sales",
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = createJson.GetProperty("id").GetString();

        var response = await client.GetAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sales", json.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ListGroups_ReturnsList()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Group A" });
        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Group B" });

        var response = await client.GetAsync("/scim/v2/Groups?startIndex=1&count=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalResults").GetInt32() >= 2);
    }

    /// <summary>
    /// A cursor-only client can page all the way through /Groups.
    /// </summary>
    /// <remarks>
    /// ServiceProviderConfig advertises <c>pagination = { cursor = true, index = false }</c> for the whole
    /// provider — <c>draft-ietf-scim-cursor-pagination</c> §4 has no per-endpoint qualifier. <c>/Users</c>
    /// honours exactly that. <c>/Groups</c> was the inverse: it bound <c>startIndex</c>/<c>count</c> only,
    /// never <c>cursor</c>, and never set <c>nextCursor</c> — so the advertised model did not exist on this
    /// collection and the unadvertised one was the only one that worked. An integrator building against the
    /// advertisement could never read past the first page of groups.
    /// </remarks>
    [Fact]
    public async Task ListGroups_PagesByCursor_AsTheServiceProviderConfigAdvertises()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = $"Cursor Group {i}" });

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var url = cursor is null
                ? "/scim/v2/Groups?count=2"
                : $"/scim/v2/Groups?count=2&cursor={Uri.EscapeDataString(cursor)}";

            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var resource in json.GetProperty("Resources").EnumerateArray())
                seen.Add(resource.GetProperty("id").GetString()!);

            cursor = json.TryGetProperty("nextCursor", out var next) && next.ValueKind is JsonValueKind.String
                ? next.GetString()
                : null;

            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    /// <summary>A cursor this server did not issue is refused, not treated as page one.</summary>
    [Fact]
    public async Task ListGroups_RejectsACursorItDidNotIssue()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync("/scim/v2/Groups?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListGroups_WithFilter_ReturnsFiltered()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Target Group", externalId = "find-me" });
        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Other Group" });

        var response = await client.GetAsync("/scim/v2/Groups?filter=displayName eq \"Target Group\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalResults").GetInt32());
    }

    /// Groups get the same grammar as users: a compound filter narrows, it does not 400 and it does not
    /// quietly return every group.
    [Fact]
    public async Task ListGroups_CompoundFilter_IsHonoured()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Engineering", externalId = "eng-1" });
        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Engineering", externalId = "eng-2" });

        var filter = Uri.EscapeDataString("displayName eq \"Engineering\" and externalId eq \"eng-2\"");
        var response = await client.GetAsync($"/scim/v2/Groups?filter={filter}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalResults").GetInt32());
        Assert.Equal("eng-2", json.GetProperty("Resources")[0].GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task ListGroups_MalformedFilter_Returns400InvalidFilter()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync($"/scim/v2/Groups?filter={Uri.EscapeDataString("displayName eq")}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalidFilter", json.GetProperty("scimType").GetString());
    }

    /// <summary>
    /// Provisions a real SCIM user and returns its id. Group membership must name users THIS client
    /// provisioned — an arbitrary id used to be stored verbatim, and because membership drives role
    /// assignment that was a privilege path. Tests therefore use real ids, as a real IdP does.
    /// </summary>
    private static async Task<string> ProvisionUserAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            active = true,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task PatchGroup_AddMembers()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Dev Team",
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = createJson.GetProperty("id").GetString();

        var m1 = await ProvisionUserAsync(client, "member-one@example.com");
        var m2 = await ProvisionUserAsync(client, "member-two@example.com");

        var patchContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[]
                {
                    new
                    {
                        op = "add",
                        path = "members",
                        value = (object)new[] { new { value = m1 }, new { value = m2 } }
                    }
                }
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Groups/{groupId}", patchContent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var members = json.GetProperty("members");
        Assert.Equal(2, members.GetArrayLength());
    }

    [Fact]
    public async Task PatchGroup_RemoveMembers()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var ua = await ProvisionUserAsync(client, "user-a@example.com");
        var ub = await ProvisionUserAsync(client, "user-b@example.com");
        var uc = await ProvisionUserAsync(client, "user-c@example.com");

        // Create group with members
        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Remove Test",
            members = new[] { new { value = ua }, new { value = ub }, new { value = uc } },
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = createJson.GetProperty("id").GetString();

        // Remove one member
        var patchContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[]
                {
                    new
                    {
                        op = "remove",
                        path = "members",
                        value = (object)new[] { new { value = ub } }
                    }
                }
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Groups/{groupId}", patchContent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var members = json.GetProperty("members");
        Assert.Equal(2, members.GetArrayLength());

        var memberValues = new List<string>();
        foreach (var m in members.EnumerateArray())
            memberValues.Add(m.GetProperty("value").GetString()!);
        Assert.Contains(ua, memberValues);
        Assert.Contains(uc, memberValues);
        Assert.DoesNotContain(ub, memberValues);
    }

    [Fact]
    public async Task PutGroup_ReplacesGroup()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Old Name",
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = createJson.GetProperty("id").GetString();

        var response = await client.PutAsJsonAsync($"/scim/v2/Groups/{groupId}", new
        {
            displayName = "New Name",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Name", json.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task DeleteGroup_Returns204()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "To Delete",
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = createJson.GetProperty("id").GetString();

        var response = await client.DeleteAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify it's gone
        var getResponse = await client.GetAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task ServiceProviderConfig_ReturnsCapabilities()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scim/v2/ServiceProviderConfig");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.False(json.GetProperty("bulk").GetProperty("supported").GetBoolean());
        Assert.True(json.GetProperty("filter").GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Schemas_ReturnsUserAndGroupSchemas()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scim/v2/Schemas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("totalResults").GetInt32());
    }

    [Fact]
    public async Task ResourceTypes_ReturnsUserAndGroup()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scim/v2/ResourceTypes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("totalResults").GetInt32());
    }

    // -----------------------------------------------------------------------
    // #44 / #147 — a group PATCH that could not be applied is not reported as applied
    // -----------------------------------------------------------------------

    /// <summary>
    /// The user endpoint learned to answer 400 for an operation it dropped; the group endpoint kept
    /// answering 200 with the unchanged resource. Group membership drives role assignment, so an operation
    /// the IdP believes landed — and never retries — is a stale entitlement at the next token issuance.
    /// </summary>
    [Theory]
    [InlineData("frobnicate", "members")]
    [InlineData("replace", "description")]
    [InlineData("remove", "displayName")]
    public async Task PatchGroup_WithAnOperationItCannotApply_Returns400InvalidPath(string op, string path)
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Reported" });
        var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patch = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[] { new { op, path, value = "x" } },
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Groups/{groupId}", patch);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalidPath", json.GetProperty("scimType").GetString());

        // And the group is untouched — a refused PATCH must not half-apply.
        var after = await (await client.GetAsync($"/scim/v2/Groups/{groupId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reported", after.GetProperty("displayName").GetString());
    }

    /// <summary>A remove with no path names nothing to remove (RFC 7644 §3.5.2).</summary>
    [Fact]
    public async Task PatchGroup_RemoveWithNoPath_Returns400NoTarget()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "NoTarget" });
        var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patch = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[] { new { op = "remove" } },
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Groups/{groupId}", patch);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("noTarget",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scimType").GetString());
    }

    /// <summary>Non-vacuity: a supported membership PATCH still answers 200 and still applies.</summary>
    [Fact]
    public async Task PatchGroup_WithASupportedOperation_StillSucceeds()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Applied" });
        var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patch = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[] { new { op = "replace", path = "displayName", value = "Applied Twice" } },
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Groups/{groupId}", patch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Applied Twice",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString());
    }

    // -----------------------------------------------------------------------
    // #347 — the listing asks the store for a page, not for everything
    // -----------------------------------------------------------------------

    /// <summary>
    /// The handler read every group in the tenant (startIndex 0, count int.MaxValue) and serialised all of
    /// them before paging in memory, on every request. It now pages at the store: count is clamped and
    /// startIndex is honoured, so the page the caller asked for is the work the request does.
    /// </summary>
    [Fact]
    public async Task ListGroups_PagesAtTheStore_AndClampsCount()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = $"Paged {i}" });

        var firstPage = await (await client.GetAsync("/scim/v2/Groups?startIndex=1&count=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, firstPage.GetProperty("totalResults").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("itemsPerPage").GetInt32());

        var secondPage = await (await client.GetAsync("/scim/v2/Groups?startIndex=3&count=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, secondPage.GetProperty("startIndex").GetInt32());
        Assert.Equal(2, secondPage.GetProperty("itemsPerPage").GetInt32());
        Assert.NotEqual(
            firstPage.GetProperty("Resources")[0].GetProperty("id").GetString(),
            secondPage.GetProperty("Resources")[0].GetProperty("id").GetString());

        // A caller asking for the whole world gets a bounded page, not the whole world. Asserted at the
        // store, because with five groups seeded the response alone cannot tell a clamped request from an
        // unclamped one — and the defect was the size of the request, not the size of the answer.
        var huge = await (await client.GetAsync("/scim/v2/Groups?count=10000000"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(huge.GetProperty("itemsPerPage").GetInt32() <= 200);
        Assert.All(_factory.ScimGroupStore.ListCalls, call => Assert.True(call.Count <= 200,
            $"the listing asked the store for {call.Count} groups"));
        Assert.Contains(_factory.ScimGroupStore.ListCalls, call => call is { StartIndex: 3, Count: 2 });
    }

    /// <summary>A filtered listing still pages, and still reports the true total once the scan completes.</summary>
    [Fact]
    public async Task ListGroups_FilteredPage_IsBounded()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Matching", externalId = $"m-{i}" });
        await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "Other" });

        var filter = Uri.EscapeDataString("displayName eq \"Matching\"");
        var page = await (await client.GetAsync($"/scim/v2/Groups?filter={filter}&count=2"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, page.GetProperty("itemsPerPage").GetInt32());
        Assert.Equal(4, page.GetProperty("totalResults").GetInt32());
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
