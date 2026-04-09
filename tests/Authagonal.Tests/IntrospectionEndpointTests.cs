using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class IntrospectionEndpointTests : IAsyncLifetime
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
    public async Task Introspect_ValidAccessToken_ReturnsActive()
    {
        var token = await _factory.GetAdminTokenAsync(_client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("active").GetBoolean());
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
    }

    [Fact]
    public async Task Introspect_ExpiredOrInvalidToken_ReturnsInactive()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "invalid.jwt.token",
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Introspect_NoToken_ReturnsInactive()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Introspect_NoClientAuth_ReturnsInactive()
    {
        var token = await _factory.GetAdminTokenAsync(_client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        });

        // No client authentication
        var response = await _client.PostAsync("/connect/introspect", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Introspect_WrongClientSecret_ReturnsInactive()
    {
        var token = await _factory.GetAdminTokenAsync(_client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = form };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, "wrong-secret");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Introspect_UserAccessToken_ReturnsSub()
    {
        var user = await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();
        var authorizeUrl = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile+email" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authResponse = await _client.GetAsync(authorizeUrl);
        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://app.test/callback",
            ["code_verifier"] = verifier,
            ["client_id"] = AuthagonalTestFactory.TestClientId
        });
        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        // Introspect — use admin client as the resource server
        var introspectForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = accessToken,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect") { Content = introspectForm };
        request.Headers.Authorization = BasicAuth(AuthagonalTestFactory.AdminClientId, AuthagonalTestFactory.AdminClientSecret);

        var response = await _client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.GetProperty("active").GetBoolean());
        Assert.Equal(user.Id, json.GetProperty("sub").GetString());
    }

    private static AuthenticationHeaderValue BasicAuth(string clientId, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
