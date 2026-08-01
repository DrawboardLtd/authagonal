using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Dynamic Client Registration (RFC 7591) — POST /connect/register.
/// The endpoint is anonymous but gated on Auth:DynamicClientRegistrationEnabled.
/// </summary>
public sealed class ClientRegistrationEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory.ConfigureAuthOptions = o => o.DynamicClientRegistrationEnabled = true;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // Feature gate
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Register_DisabledByDefault_Returns403NotSupported()
    {
        // A factory WITHOUT the option flipped — DCR must be off out of the box.
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Blocked App",
            redirect_uris = new[] { "https://blocked.example/callback" }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_supported", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Successful registration
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Register_PublicClient_SucceedsWithoutSecret()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Public SPA",
            redirect_uris = new[] { "https://spa.example/callback" },
            grant_types = new[] { "authorization_code" },
            token_endpoint_auth_method = "none",
            scope = "openid profile email"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var clientId = json.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrEmpty(clientId));

        // Public client (auth method "none"): no secret is minted or returned.
        Assert.False(json.TryGetProperty("client_secret", out _));
        Assert.Equal("none", json.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("Public SPA", json.GetProperty("client_name").GetString());
        Assert.Contains("code", json.GetProperty("response_types").EnumerateArray().Select(e => e.GetString()));

        // Stored client reflects the public profile, with PKCE forced on.
        var stored = await _factory.ClientStore.GetAsync(clientId!);
        Assert.NotNull(stored);
        Assert.False(stored!.RequireClientSecret);
        Assert.Empty(stored.ClientSecretHashes);
        Assert.True(stored.RequirePkce);
        Assert.Equal(["https://spa.example/callback"], stored.RedirectUris);
        Assert.Equal(["openid", "profile", "email"], stored.AllowedScopes);
    }

    [Fact]
    public async Task Register_ConfidentialClient_IssuesSecret()
    {
        // Default token_endpoint_auth_method is client_secret_basic → confidential.
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Server App",
            redirect_uris = new[] { "https://server.example/signin-oidc" },
            grant_types = new[] { "authorization_code", "refresh_token" },
            scope = "openid offline_access"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var clientId = json.GetProperty("client_id").GetString()!;
        var clientSecret = json.GetProperty("client_secret").GetString();
        Assert.False(string.IsNullOrEmpty(clientSecret));
        Assert.Equal(0, json.GetProperty("client_secret_expires_at").GetInt64());
        Assert.Equal("client_secret_basic", json.GetProperty("token_endpoint_auth_method").GetString());

        // Stored client is confidential: a single secret hash, never the plaintext.
        var stored = await _factory.ClientStore.GetAsync(clientId);
        Assert.NotNull(stored);
        Assert.True(stored!.RequireClientSecret);
        var hash = Assert.Single(stored.ClientSecretHashes);
        Assert.NotEqual(clientSecret, hash);

        // offline_access / refresh_token grant → offline access enabled.
        Assert.True(stored.AllowOfflineAccess);
        Assert.Contains("refresh_token", stored.AllowedGrantTypes);
    }

    // -----------------------------------------------------------------------
    // Grant-type allowlist
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("client_credentials")]
    [InlineData(GrantTypes.DeviceCode)]
    [InlineData("implicit")]
    public async Task Register_DisallowedGrantType_Returns400(string grantType)
    {
        // Open registration may only mint authorization_code + refresh_token clients.
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Machine Client",
            grant_types = new[] { grantType },
            redirect_uris = new[] { "https://machine.example/callback" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client_metadata", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Redirect-URI scheme filtering
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("not-an-absolute-uri")]
    public async Task Register_DangerousRedirectUri_Returns400(string redirectUri)
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Sneaky App",
            redirect_uris = new[] { redirectUri }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_redirect_uri", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_NativeCustomScheme_IsAccepted()
    {
        // Mobile deep-link schemes stay valid — only script/data/file pseudo-schemes are blocked.
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Mobile App",
            redirect_uris = new[] { "com.example.app://oauth/callback" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_AuthorizationCodeWithoutRedirectUris_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "No Redirects",
            grant_types = new[] { "authorization_code" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client_metadata", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Scope validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Register_AdminScope_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Wannabe Admin",
            redirect_uris = new[] { "https://evil.example/callback" },
            scope = $"openid {AuthagonalTestFactory.AdminScope}"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_UnknownScope_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Scope Fisher",
            redirect_uris = new[] { "https://app.example/callback" },
            scope = "openid made-up-scope"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", json.GetProperty("error").GetString());
        Assert.Contains("made-up-scope", json.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Register_StoreDefinedScope_IsRefusedUnlessTheOperatorOpenedIt()
    {
        // Existence in the scope store used to BE the allowlist, so an anonymous registrant could
        // declare every API scope the deployment had ever defined simply because it existed. A scope
        // exists because some client needs it, not because everyone may claim it — registration now
        // reaches the four built-ins plus whatever Auth:DynamicClientRegistrationScopes names.
        await _factory.ScopeStore.CreateAsync(new Scope { Name = "orders.read" });

        var response = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Orders App",
            redirect_uris = new[] { "https://orders.example/callback" },
            scope = "openid orders.read"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Rate limiting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Register_EleventhRequestFromSameIp_Returns429()
    {
        // The endpoint allows 10 registration attempts per IP per hour; the 11th is limited.
        // (Every request after the feature-gate check counts, valid or not.)
        for (var i = 0; i < 10; i++)
        {
            var ok = await _client.PostAsJsonAsync("/connect/register", new
            {
                client_name = $"Flood {i}",
                redirect_uris = new[] { $"https://flood{i}.example/callback" },
                token_endpoint_auth_method = "none"
            });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var limited = await _client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Flood 11",
            redirect_uris = new[] { "https://flood11.example/callback" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var json = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", json.GetProperty("error").GetString());
    }
}
