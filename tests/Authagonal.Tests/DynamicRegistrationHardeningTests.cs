using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// What an anonymous registrant may declare about itself. This is the only client-mutation path with
/// no caller identity at all, so every check the authenticated paths run has to be reproduced here.
/// </summary>
public sealed class DynamicRegistrationHardeningTests : IAsyncLifetime
{
    // DCR is off by default (Auth:DynamicClientRegistrationEnabled) — which is itself the right
    // default, and why these checks matter only for deployments that opt in.
    private readonly AuthagonalTestFactory _factory = new()
    {
        ConfigureAuthOptions = o => o.DynamicClientRegistrationEnabled = true,
    };
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task CleartextHttpRedirect_IsRefused()
    {
        // An authorization code — and with it the whole authorization — would travel to an arbitrary
        // host over a link any on-path party can read and modify.
        var response = await RegisterAsync("http://attacker.example/cb");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoopbackHttpRedirect_IsStillAllowed()
    {
        // RFC 8252 §7.3 requires this for native apps.
        var response = await RegisterAsync("http://127.0.0.1:8080/cb");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RedirectWithFragment_IsRefused()
    {
        // RFC 6749 §3.1.2 forbids it, and the fragment is where an implicit-style response puts a
        // token — so registering one is either a mistake or an attempt to shape where credentials land.
        var response = await RegisterAsync("https://app.example/cb#tokens");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RoleGatedScope_CannotBeSelfAssigned()
    {
        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "restricted.read",
            AllowedRoles = ["operator"],
        });

        // Existence in the scope store was the only test, so an anonymous registrant could
        // self-assign any scope the deployment had defined — including operator-restricted ones.
        // Every authenticated client-mutation path runs these through IClientScopeGuard; this one,
        // the only unauthenticated path, ran nothing.
        var response = await RegisterAsync("https://app.example/cb", scope: "openid restricted.read");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnrestrictedScope_IsStillRegistrable()
    {
        var response = await RegisterAsync("https://app.example/cb", scope: "openid profile");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// An anonymous registrant cannot name an internal address as a logout URI.
    /// </summary>
    /// <remarks>
    /// Both are dereferenced by the SERVER — back-channel is an outbound POST from the logout path,
    /// front-channel is framed into the logged-out user's browser — and this is the one client-mutation
    /// path with no caller at all, so registering
    /// <c>backchannel_logout_uri=http://169.254.169.254/…</c> was unauthenticated SSRF with an
    /// attacker-chosen target. The check existed but nothing pinned it.
    /// </remarks>
    [Theory]
    [InlineData("backchannel_logout_uri", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("backchannel_logout_uri", "http://127.0.0.1:9200/_shutdown")]
    [InlineData("backchannel_logout_uri", "http://10.1.2.3/logout")]
    [InlineData("backchannel_logout_uri", "http://admin.internal/logout")]
    [InlineData("frontchannel_logout_uri", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("frontchannel_logout_uri", "http://localhost/logout")]
    public async Task InternalLogoutUri_IsRefused(string field, string uri)
    {
        var body = new Dictionary<string, object>
        {
            ["client_name"] = "Test App",
            ["redirect_uris"] = new[] { "https://app.example/cb" },
            ["grant_types"] = new[] { "authorization_code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = "openid",
            [field] = uri,
        };

        var response = await _client.PostAsJsonAsync("/connect/register", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client_metadata", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExternalLogoutUri_IsAccepted()
    {
        var body = new Dictionary<string, object>
        {
            ["client_name"] = "Test App",
            ["redirect_uris"] = new[] { "https://app.example/cb" },
            ["grant_types"] = new[] { "authorization_code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = "openid",
            ["backchannel_logout_uri"] = "https://app.example/backchannel-logout",
        };

        var response = await _client.PostAsJsonAsync("/connect/register", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<HttpResponseMessage> RegisterAsync(string redirectUri, string scope = "openid") =>
        _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Test App",
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code" },
            token_endpoint_auth_method = "none",
            scope,
        });
}
