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
                        value = (object)new[] { new { value = "user-123" }, new { value = "user-456" } }
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

        // Create group with members
        var createResponse = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Remove Test",
            members = new[] { new { value = "user-a" }, new { value = "user-b" }, new { value = "user-c" } },
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
                        value = (object)new[] { new { value = "user-b" } }
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
        Assert.Contains("user-a", memberValues);
        Assert.Contains("user-c", memberValues);
        Assert.DoesNotContain("user-b", memberValues);
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

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
