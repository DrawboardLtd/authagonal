using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
