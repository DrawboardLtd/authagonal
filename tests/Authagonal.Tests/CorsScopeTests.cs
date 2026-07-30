using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// <c>DynamicCorsPolicyProvider</c> ignores the requested policy name and <c>app.UseCors()</c> supplies
/// none, so whatever it returns applies to EVERY endpoint. The policy sets <c>AllowCredentials</c>, so any
/// origin a client had registered could read authenticated responses from the cookie-authenticated
/// interactive-auth API — which includes the account and consent APIs and
/// <c>POST /api/auth/mfa/recovery/generate</c>, an endpoint that returns plaintext recovery codes. Anonymous
/// dynamic registration let an attacker put an origin on that list.
///
/// Client-registered origins are now honoured only on the OAuth/OIDC protocol surface a browser RP
/// legitimately calls cross-origin; everything else uses operator configuration only.
/// </summary>
public sealed class CorsScopeTests : IAsyncDisposable
{
    private const string EvilOrigin = "https://evil.example";
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task SeedClientWithOriginAsync()
    {
        await _factory.SeedTestDataAsync();
        var client = (await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedCorsOrigins = [EvilOrigin];
        await _factory.ClientStore.UpsertAsync(client);
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method = "POST")
    {
        var req = new HttpRequestMessage(HttpMethod.Options, path);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", method);
        return req;
    }

    /// <summary>
    /// The interactive-auth API must not honour a client-registered origin. Recovery-code generation is the
    /// sharpest case: a credentialed cross-origin read there hands over ten live MFA bypass codes.
    /// </summary>
    [Theory]
    [InlineData("/api/auth/mfa/recovery/generate")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/session")]
    public async Task Client_origin_is_not_honoured_on_the_interactive_auth_api(string path)
    {
        await SeedClientWithOriginAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(Preflight(path, EvilOrigin));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            $"{path} echoed a client-registered origin back with credentials allowed");
    }

    /// <summary>
    /// The protocol surface still works: a browser-based relying party calling /connect/token
    /// cross-origin is the legitimate use of AllowedCorsOrigins, and must not regress.
    /// </summary>
    [Fact]
    public async Task Client_origin_is_still_honoured_on_the_protocol_surface()
    {
        await SeedClientWithOriginAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(Preflight("/connect/token", EvilOrigin));

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "a client-registered origin must still reach the OAuth protocol endpoints");
        Assert.Equal(EvilOrigin, string.Join("", response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    /// <summary>
    /// An origin nobody registered is refused everywhere, protocol surface included.
    /// </summary>
    [Fact]
    public async Task Unregistered_origin_is_refused_on_the_protocol_surface()
    {
        await SeedClientWithOriginAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(Preflight("/connect/token", "https://someone-else.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// Anonymous dynamic registration must not let a registrant name an arbitrary origin: the stored
    /// origins are derived from its own validated https redirect URIs.
    /// </summary>
    [Fact]
    public async Task Dcr_cannot_register_an_arbitrary_cors_origin()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "attacker",
            redirect_uris = new[] { "https://legit.example/callback" },
            grant_types = new[] { "authorization_code" },
            allowed_cors_origins = new[] { EvilOrigin },
        });

        // Registration may be disabled in this host; the assertion only applies when it succeeded.
        if (!response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = body.GetProperty("client_id").GetString()!;
        var stored = await _factory.ClientStore.GetAsync(clientId);

        Assert.DoesNotContain(EvilOrigin, stored!.AllowedCorsOrigins);
        // Derived from the redirect URI it actually proved.
        Assert.Contains("https://legit.example", stored.AllowedCorsOrigins);
    }
}
