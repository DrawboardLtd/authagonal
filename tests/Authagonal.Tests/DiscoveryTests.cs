using System.Net;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class DiscoveryTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task OpenIdConfiguration_ReturnsValidDocument()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(AuthagonalTestFactory.TestIssuer, json.GetProperty("issuer").GetString());
        Assert.Contains("authorization_code", json.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("openid", json.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("S256", json.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task OpenIdConfiguration_ContainsRequiredEndpoints()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.NotNull(json.GetProperty("authorization_endpoint").GetString());
        Assert.NotNull(json.GetProperty("token_endpoint").GetString());
        Assert.NotNull(json.GetProperty("userinfo_endpoint").GetString());
        Assert.NotNull(json.GetProperty("jwks_uri").GetString());
        Assert.NotNull(json.GetProperty("end_session_endpoint").GetString());
    }

    [Fact]
    public async Task Jwks_ReturnsKeySet()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration/jwks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = json.GetProperty("keys");

        Assert.True(keys.GetArrayLength() > 0, "JWKS should contain at least one key");

        var firstKey = keys[0];
        Assert.Equal("EC", firstKey.GetProperty("kty").GetString());
        Assert.Equal("sig", firstKey.GetProperty("use").GetString());
        Assert.Equal("ES256", firstKey.GetProperty("alg").GetString());
        Assert.Equal("P-256", firstKey.GetProperty("crv").GetString());
        Assert.NotNull(firstKey.GetProperty("x").GetString());
        Assert.NotNull(firstKey.GetProperty("y").GetString());
    }
}
