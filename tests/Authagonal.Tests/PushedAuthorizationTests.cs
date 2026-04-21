using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class PushedAuthorizationTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Par_PublicClient_Returns201WithRequestUri()
    {
        var form = BuildPushedForm();
        var response = await _client.PostAsync("/connect/par", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = json.GetProperty("request_uri").GetString();
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", requestUri);
        Assert.Equal(90, json.GetProperty("expires_in").GetInt32());
    }

    [Fact]
    public async Task Par_RequestUriInBody_Rejected()
    {
        var fields = BasePushedFields();
        fields["request_uri"] = "urn:ietf:params:oauth:request_uri:spoof";
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Par_UnknownClient_Returns401()
    {
        var fields = BasePushedFields();
        fields["client_id"] = "nonexistent-client";
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Par_ConfidentialClientMissingSecret_Returns401()
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["response_type"] = "code",
            ["scope"] = "openid",
        };
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_UnknownRequestUri_ReturnsInvalidRequest()
    {
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString("urn:ietf:params:oauth:request_uri:bogus")}";

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_PushedRequestForDifferentClient_Rejected()
    {
        // Push as TestClient.
        var parForm = BuildPushedForm();
        var parResponse = await _client.PostAsync("/connect/par", parForm);
        var parJson = await parResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = parJson.GetProperty("request_uri").GetString()!;

        // Try to consume as AdminClient — should fail cleanly without leaking.
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.AdminClientId}&request_uri={Uri.EscapeDataString(requestUri)}";
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Par_ThenAuthorize_Unauthenticated_RedirectsToLoginWithRequestUri()
    {
        var parResponse = await _client.PostAsync("/connect/par", BuildPushedForm());
        var parJson = await parResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = parJson.GetProperty("request_uri").GetString()!;

        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString(requestUri)}";
        var response = await _client.GetAsync(url);

        // Should redirect to login — request_uri must NOT be consumed yet, so the user can
        // round-trip through login and retry.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());

        // Re-hitting the same /authorize should still succeed (load, not consume).
        var retry = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
    }

    [Fact]
    public async Task Discovery_AdvertisesParEndpoint()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var endpoint = json.GetProperty("pushed_authorization_request_endpoint").GetString();
        Assert.NotNull(endpoint);
        Assert.EndsWith("/connect/par", endpoint);
    }

    private static FormUrlEncodedContent BuildPushedForm() => new(BasePushedFields());

    private static Dictionary<string, string> BasePushedFields() => new()
    {
        ["client_id"] = AuthagonalTestFactory.TestClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = "https://app.test/callback",
        ["scope"] = "openid profile",
        ["state"] = "xyz",
        ["code_challenge"] = GenerateCodeChallenge("verifier-of-sufficient-length-1234"),
        ["code_challenge_method"] = "S256",
    };

    private static string GenerateCodeChallenge(string verifier)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
