using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class SamlEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _connectionId = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);

        // Create SAML connection via admin API
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new
            {
                connectionName = "Test IdP",
                entityId = "https://idp.test",
                metadataLocation = "https://idp.test/metadata",
                allowedDomains = new[] { "example.com" }
            }));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        _connectionId = json.GetProperty("connectionId").GetString()!;
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
    public async Task SamlLogin_InvalidConnection_Returns404()
    {
        var response = await _client.GetAsync("/saml/nonexistent/login");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SamlAcs_MissingSamlResponse_Returns400()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>());
        var response = await _client.PostAsync($"/saml/{_connectionId}/acs", form);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
            $"Expected 400 or 422, got {response.StatusCode}");
    }

    [Fact]
    public async Task SamlMetadata_ReturnsValidXml()
    {
        var response = await _client.GetAsync($"/saml/{_connectionId}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("EntityDescriptor", content);
        Assert.Contains("AssertionConsumerService", content);
    }

    [Fact]
    public async Task SamlMetadata_InvalidConnection_Returns404()
    {
        var response = await _client.GetAsync("/saml/nonexistent/metadata");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SsoCheck_KnownDomain_ReturnsSsoRequired()
    {
        var response = await _client.GetAsync("/api/auth/sso-check?email=user@example.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ssoRequired").GetBoolean());
    }

    [Fact]
    public async Task SsoCheck_UnknownDomain_ReturnsNotRequired()
    {
        var response = await _client.GetAsync("/api/auth/sso-check?email=user@unknown.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("ssoRequired").GetBoolean());
    }

    [Fact]
    public async Task SamlAcs_InvalidXml_ReturnsError()
    {
        var badResponse = Convert.ToBase64String(Encoding.UTF8.GetBytes("<not-saml>broken</not-saml>"));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = badResponse,
        });

        var response = await _client.PostAsync($"/saml/{_connectionId}/acs", form);
        Assert.True((int)response.StatusCode >= 400, $"Expected error, got {response.StatusCode}");
    }
}
