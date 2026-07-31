using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Where a pushed request is validated, and how a native app's loopback redirect is matched.
/// </summary>
public sealed class ParAndRedirectMatchingTests : IAsyncLifetime
{
    private const string NativeClientId = "native-app";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = NativeClientId,
            ClientName = "Native App",
            RequireClientSecret = false,
            RequirePkce = false,
            AllowedGrantTypes = ["authorization_code"],
            AllowedScopes = ["openid"],
            // Registered with a placeholder port, as RFC 8252 §7.3 expects a native app to do.
            RedirectUris = ["http://127.0.0.1:0/callback"],
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F74 — loopback redirect URIs match on any port
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LoopbackRedirect_MatchesOnADifferentPort()
    {
        await LoginAsync();

        // A native app binds an ephemeral port at runtime and cannot know it at registration time.
        // Requiring an exact match meant it either failed on a port it could not predict, or had to
        // register a fixed one — which §7.3 exists to avoid, since a fixed port can be squatted by
        // another local process.
        var response = await _client.GetAsync(
            $"/connect/authorize?client_id={NativeClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:53127/callback")}" +
            "&response_type=code&scope=openid&state=t");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://127.0.0.1:53127/callback", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonLoopbackRedirect_StillRequiresAnExactPort()
    {
        await LoginAsync();

        // The relaxation is loopback-only; a public host must still match exactly, or registration
        // would stop constraining where codes are delivered.
        var response = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test:9999/callback")}" +
            "&response_type=code&scope=openid&state=t");

        var location = response.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain("app.test:9999", location);
    }

    // -----------------------------------------------------------------------
    // F305 — a pushed request is validated when it is pushed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Par_UnregisteredRedirectUri_IsRefusedAtPushTime()
    {
        // RFC 9126 §2.1 step 3. Storing whatever was posted meant an invalid request got a 201 and a
        // request_uri, and failed only at /connect/authorize — where the error surfaces to the END
        // USER mid-flow rather than to the client that made the mistake.
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = NativeClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = "https://attacker.example/cb",
                ["scope"] = "openid",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Par_ValidRequest_IsStillAccepted()
    {
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = NativeClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = "http://127.0.0.1:0/callback",
                ["scope"] = "openid",
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(json.GetProperty("request_uri").GetString());
    }

    private async Task LoginAsync()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
    }
}
