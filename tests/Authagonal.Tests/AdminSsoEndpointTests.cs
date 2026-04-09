using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class AdminSsoEndpointTests : IAsyncLifetime
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

    // ── SAML connections ──

    [Fact]
    public async Task CreateSamlConnection_ReturnsConnection()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new
            {
                connectionName = "Okta",
                entityId = "https://okta.test/saml",
                metadataLocation = "https://okta.test/metadata",
                allowedDomains = new[] { "okta.test" }
            }));

        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Okta", json.GetProperty("connectionName").GetString());
        Assert.NotNull(json.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task GetSamlConnection_ReturnsDetails()
    {
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new { connectionName = "Azure AD", entityId = "https://aad.test", metadataLocation = "https://aad.test/meta" }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("connectionId").GetString()!;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/v1/saml/connections/{id}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azure AD", json.GetProperty("connectionName").GetString());
    }

    [Fact]
    public async Task DeleteSamlConnection_Returns204()
    {
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new { connectionName = "Temp SAML", entityId = "https://temp.test", metadataLocation = "https://temp.test/meta" }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("connectionId").GetString()!;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/saml/connections/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── OIDC connections ──

    [Fact]
    public async Task CreateOidcConnection_ReturnsConnection()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Google",
                metadataLocation = "https://accounts.google.com/.well-known/openid-configuration",
                clientId = "google-client-id",
                clientSecret = "google-secret",
                redirectUrl = "https://test.authagonal.local/oidc/callback",
                allowedDomains = new[] { "gmail.com" }
            }));

        Assert.True(response.IsSuccessStatusCode, $"Got {response.StatusCode}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Google", json.GetProperty("connectionName").GetString());
        Assert.NotNull(json.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task DeleteOidcConnection_Returns204()
    {
        var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Temp OIDC",
                metadataLocation = "https://temp.test/.well-known/openid-configuration",
                clientId = "temp-client",
                clientSecret = "temp-secret",
                redirectUrl = "https://test.authagonal.local/oidc/callback",
            }));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("connectionId").GetString()!;

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/oidc/connections/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── SSO domains ──

    [Fact]
    public async Task ListSsoDomains_ReturnsRegisteredDomains()
    {
        // Create a SAML connection with domains
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new
            {
                connectionName = "Domain Test",
                entityId = "https://domain.test",
                metadataLocation = "https://domain.test/meta",
                allowedDomains = new[] { "domain.test" }
            }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/sso/domains"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(json);
        Assert.Contains(json, d => d.GetProperty("domain").GetString() == "domain.test");
    }

    [Fact]
    public async Task SsoEndpoints_RequireAdminToken()
    {
        var response = await _client.GetAsync("/api/v1/sso/domains");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
