using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Covers the public <c>/api/auth/providers</c> endpoint that drives the login screen's
/// "Continue with {name}" buttons. The contract under test:
///   - both OIDC and SAML connections are surfaced (SAML used to be invisible — buttons only);
///   - only connections WITHOUT AllowedDomains appear (domain-routed ones are reached email-first
///     via /sso-check, so a button would be redundant);
///   - each provider carries its protocol type, branding icon, and the correct SP-init loginUrl.
/// </summary>
public sealed class ProvidersEndpointTests : IAsyncLifetime
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

    private async Task<string> CreateAsync(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var res = await _client.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, $"create {path} → {res.StatusCode}");
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("connectionId").GetString()!;
    }

    [Fact]
    public async Task Providers_SurfaceOidcAndSaml_Buttons_ExcludingDomainRouted()
    {
        // Button connections (no AllowedDomains) — should each render a "Continue with X" button.
        var oidcId = await CreateAsync("/api/v1/oidc/connections", new
        {
            connectionName = "Google",
            iconUrl = "https://cdn.test/google.svg",
            metadataLocation = "https://accounts.google.com/.well-known/openid-configuration",
            clientId = "gid",
            clientSecret = "gsecret",
            redirectUrl = "https://t.test/oidc/callback",
        });
        var samlId = await CreateAsync("/api/v1/saml/connections", new
        {
            connectionName = "Okta",
            iconUrl = "https://cdn.test/okta.svg",
            entityId = "https://okta.test",
            metadataLocation = "https://okta.test/meta",
        });
        // Domain-routed connection (has AllowedDomains) — reached email-first, must NOT be a button.
        await CreateAsync("/api/v1/oidc/connections", new
        {
            connectionName = "Acme Domain SSO",
            metadataLocation = "https://acme.test/.well-known/openid-configuration",
            clientId = "aid",
            clientSecret = "asecret",
            redirectUrl = "https://t.test/oidc/callback",
            allowedDomains = new[] { "acme.test" },
        });

        var res = await _client.GetAsync("/api/auth/providers");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var providers = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("providers").EnumerateArray().ToList();

        // Domain-routed connection is excluded from the button list.
        Assert.DoesNotContain(providers, p => p.GetProperty("name").GetString() == "Acme Domain SSO");

        var oidc = providers.Single(p => p.GetProperty("connectionId").GetString() == oidcId);
        Assert.Equal("oidc", oidc.GetProperty("type").GetString());
        Assert.Equal("https://cdn.test/google.svg", oidc.GetProperty("iconUrl").GetString());
        Assert.Equal($"/oidc/{oidcId}/login", oidc.GetProperty("loginUrl").GetString());

        var saml = providers.Single(p => p.GetProperty("connectionId").GetString() == samlId);
        Assert.Equal("saml", saml.GetProperty("type").GetString());   // SAML now surfaces as a button
        Assert.Equal("https://cdn.test/okta.svg", saml.GetProperty("iconUrl").GetString());
        Assert.Equal($"/saml/{samlId}/login", saml.GetProperty("loginUrl").GetString());
    }
}
