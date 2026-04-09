using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class AdminRoleEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task ListRoles_Empty_ReturnsEmptyList()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/roles"));
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"Expected OK or Created, got {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("roles").GetArrayLength() == 0);
    }

    [Fact]
    public async Task CreateRole_ReturnsRole()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "editor", description = "Can edit content" }));
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"Expected OK or Created, got {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("editor", json.GetProperty("name").GetString());
        Assert.Equal("Can edit content", json.GetProperty("description").GetString());
    }

    [Fact]
    public async Task CreateRole_DuplicateName_Returns409()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "admin" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "admin" }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetRole_Exists_ReturnsRole()
    {
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "viewer" }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var roleId = created.GetProperty("id").GetString()!;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/v1/roles/{roleId}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"Expected OK or Created, got {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("viewer", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task DeleteRole_Exists_Returns204()
    {
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "temp-role" }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var roleId = created.GetProperty("id").GetString()!;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/roles/{roleId}"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await _client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/v1/roles/{roleId}"));
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task AssignRole_ToUser_Works()
    {
        var user = await _factory.SeedTestUserAsync();

        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "manager" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles/assign",
            new { userId = user.Id, roleName = "manager" }));
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"Expected OK or Created, got {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("manager", json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task UnassignRole_FromUser_Works()
    {
        var user = await _factory.SeedTestUserAsync();

        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "temp-assign" }));
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles/assign",
            new { userId = user.Id, roleName = "temp-assign" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles/unassign",
            new { userId = user.Id, roleName = "temp-assign" }));
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"Expected OK or Created, got {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("temp-assign", json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Roles_RequireAdminToken()
    {
        var response = await _client.GetAsync("/api/v1/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
