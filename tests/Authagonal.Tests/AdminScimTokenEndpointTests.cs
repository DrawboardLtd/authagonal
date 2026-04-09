using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class AdminScimTokenEndpointTests : IAsyncLifetime
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
    public async Task CreateScimToken_ReturnsTokenWithRawValue()
    {
        // First create a client for the SCIM token
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "scim-test-client",
            ClientName = "SCIM Test"
        });

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-test-client", description = "Test SCIM token" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("tokenId").GetString());
        Assert.NotNull(json.GetProperty("token").GetString());
        Assert.Equal("scim-test-client", json.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task ListScimTokens_ReturnsCreatedTokens()
    {
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "scim-list-client",
            ClientName = "SCIM List"
        });

        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-list-client", description = "Token 1" }));
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-list-client", description = "Token 2" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get,
            "/api/v1/scim/tokens?clientId=scim-list-client"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tokens = json.GetProperty("tokens");
        Assert.Equal(2, tokens.GetArrayLength());
    }

    [Fact]
    public async Task DeleteScimToken_RemovesToken()
    {
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "scim-delete-client",
            ClientName = "SCIM Delete"
        });

        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-delete-client", description = "To be deleted" }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tokenId = created.GetProperty("tokenId").GetString()!;

        var deleteResponse = await _client.SendAsync(AdminRequest(HttpMethod.Delete,
            $"/api/v1/scim/tokens/{tokenId}?clientId=scim-delete-client"));
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var listResponse = await _client.SendAsync(AdminRequest(HttpMethod.Get,
            "/api/v1/scim/tokens?clientId=scim-delete-client"));
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tokens = listJson.GetProperty("tokens");
        // Token is revoked, not deleted — it still appears in the list
        Assert.Equal(1, tokens.GetArrayLength());
        Assert.True(tokens[0].GetProperty("isRevoked").GetBoolean());
    }

    [Fact]
    public async Task ScimTokens_RequireAdminToken()
    {
        var response = await _client.GetAsync("/api/v1/scim/tokens?clientId=test");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
