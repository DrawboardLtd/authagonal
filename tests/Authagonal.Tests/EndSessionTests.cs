using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class EndSessionTests : IAsyncLifetime
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
    public async Task EndSession_Get_SignsOutAndReturnsMessage()
    {
        // Login first
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Verify session exists
        var sessionResponse = await _client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);

        // End session
        var response = await _client.GetAsync("/connect/endsession");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("message").GetString());

        // Session should be gone
        var afterResponse = await _client.GetAsync("/api/auth/session");
        Assert.NotEqual(HttpStatusCode.OK, afterResponse.StatusCode);
    }

    [Fact]
    public async Task EndSession_Post_SignsOutAndReturnsMessage()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var form = new FormUrlEncodedContent(new Dictionary<string, string>());
        var response = await _client.PostAsync("/connect/endsession", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EndSession_WithValidIdTokenHintAndRedirectUri_Redirects()
    {
        // Login and get tokens via PKCE flow
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
        var idToken = tokens.GetProperty("id_token").GetString()!;

        // End session with id_token_hint + registered post_logout_redirect_uri
        var endSessionUrl = $"/connect/endsession?id_token_hint={Uri.EscapeDataString(idToken)}" +
            $"&post_logout_redirect_uri={Uri.EscapeDataString("https://app.test")}" +
            $"&state=logout123";

        var response = await _client.GetAsync(endSessionUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://app.test", location);
        Assert.Contains("state=logout123", location);
    }

    [Fact]
    public async Task EndSession_WithUnregisteredRedirectUri_DoesNotRedirect()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Use a redirect_uri not registered on any client
        var response = await _client.GetAsync(
            "/connect/endsession?post_logout_redirect_uri=https://evil.com/logout");

        // Should not redirect — returns OK with message
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EndSession_NotAuthenticated_StillReturnsOk()
    {
        var response = await _client.GetAsync("/connect/endsession");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

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
