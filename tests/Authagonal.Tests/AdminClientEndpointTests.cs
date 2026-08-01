using Authagonal.Server.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints.Admin;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

// ---------------------------------------------------------------------------------------------
// Test host for the admin client + provisioning endpoints.
//
// AuthagonalTestFactory does not register IAuditLogger / IClientScopeGuard / IProvisioningAppQuota
// (production defaults come from AddAuthagonal, which the factory mirrors but doesn't call) nor any
// IProvisioningAppStore. Minimal-API binding then treats those unregistered interface parameters as
// inferred-body parameters, so every client create/update/delete and every provisioning route 400s
// before reaching the handler. This bespoke host maps ONLY those endpoint groups with the missing
// services registered (using the same production default implementations), behind a simple bearer
// scheme satisfying the endpoints' "IdentityAdmin" policy requirement.
// ---------------------------------------------------------------------------------------------
internal sealed class AdminSurfaceHost : IAsyncDisposable
{
    public const string AdminBearer = "surface-admin-token";

    public InMemoryClientStore ClientStore { get; } = new();
    public InMemoryProvisioningAppStore ProvisioningAppStore { get; } = new();

    /// <summary>Set before the first CreateClient() to mock the "Provisioning" outbound HttpClient.</summary>
    public HttpMessageHandler? ProvisioningHttpHandler { get; set; }

    /// <summary>Set before the first CreateClient() to cap provisioning-app creation.</summary>
    public int? MaxProvisioningApps { get; set; }

    /// <summary>
    /// Records what the client-mutation paths publish on the cluster bus. The CORS origin list is
    /// pooled from the client table and cached for an hour with no invalidation, so a write that does
    /// not announce itself leaves a revoked origin credentialed on every warm node.
    /// </summary>
    public RecordingClusterEventBus ClusterEvents { get; } = new();

    /// <summary>
    /// Set before the first CreateClient() to substitute the privilege gate. Defaults to the shipped
    /// single-role implementation, which grants everything — so the endpoints' denial branches are
    /// unreachable unless a test supplies a guard that actually refuses something.
    /// </summary>
    public IClientScopeGuard ScopeGuard { get; set; } = new AllowAllClientScopeGuard();

    private WebApplication? _app;

    public HttpClient CreateClient(bool authenticated = true)
    {
        EnsureStarted();
        var server = _app!.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");
        var client = server.CreateClient();
        if (authenticated)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminBearer);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private void EnsureStarted()
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddSingleton<IClientStore>(ClientStore);
        services.AddSingleton<IProvisioningAppStore>(ProvisioningAppStore);
        // Production defaults (registered by AddAuthagonal via TryAdd):
        services.AddSingleton<IAuditLogger, NullAuditLogger>();
        services.AddSingleton<IClientScopeGuard>(ScopeGuard);
        services.AddSingleton<IProvisioningAppQuota>(new FixedProvisioningAppQuota(MaxProvisioningApps));
        services.AddSingleton<Authagonal.Core.Clustering.IClusterEventBus>(ClusterEvents);

        if (ProvisioningHttpHandler is not null)
            services.AddHttpClient("Provisioning").ConfigurePrimaryHttpMessageHandler(() => ProvisioningHttpHandler);
        else
            services.AddHttpClient("Provisioning");

        services.AddAuthentication("TestAdmin")
            .AddScheme<AuthenticationSchemeOptions, HeaderAdminAuthHandler>("TestAdmin", null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy("IdentityAdmin", policy =>
            {
                policy.AddAuthenticationSchemes("TestAdmin");
                policy.RequireAuthenticatedUser();
            });
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapClientAdminEndpoints();
        _app.MapProvisioningAdminEndpoints();
        _app.StartAsync().GetAwaiter().GetResult();
    }
}

/// <summary>Cluster bus that records published topics, so a test can assert a write announced itself.</summary>
internal sealed class RecordingClusterEventBus : Authagonal.Core.Clustering.IClusterEventBus
{
    public List<string> Published { get; } = [];

    public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        Published.Add(topic);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
        => new Noop();

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

internal sealed class HeaderAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.ToString() == $"Bearer {AdminSurfaceHost.AdminBearer}")
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("sub", "test-admin"),
                new Claim("email", "admin@test.example"),
                new Claim("scope", "authagonal-admin"),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

internal sealed class InMemoryProvisioningAppStore : IProvisioningAppStore
{
    private readonly ConcurrentDictionary<string, ProvisioningAppConfig> _apps = new();

    public Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default)
        => Task.FromResult(_apps.GetValueOrDefault(appId));

    public Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProvisioningAppConfig>>(_apps.Values.ToList());

    public Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default)
    {
        _apps[app.AppId] = app;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        _apps.TryRemove(appId, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FixedProvisioningAppQuota(int? max) : IProvisioningAppQuota
{
    public Task<int?> GetMaxAsync(CancellationToken ct = default) => Task.FromResult(max);
}

/// <summary>Mock outbound handler for the provisioning /test endpoint: records requests, returns a
/// canned response or throws.</summary>
internal sealed class RecordingProvisioningHandler : HttpMessageHandler
{
    public List<(string Url, string? Authorization, string Body)> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
    public Exception? Throws { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
        if (Throws is not null) throw Throws;
        return Responder?.Invoke(request)
            ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
    }
}

// ---------------------------------------------------------------------------------------------
// Client admin CRUD (bespoke host — see AdminSurfaceHost note above).
// ---------------------------------------------------------------------------------------------
public sealed class AdminClientEndpointTests : IAsyncLifetime
{
    private readonly AdminSurfaceHost _host = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> CreateAsync(object body)
        => await _client.PostAsync("/api/v1/clients/", Json(body));

    // -------------------------------------------------------------------------
    // POST /api/v1/clients — CreateClient
    // -------------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // F328 — the admin client API applies the same redirect-URI rules as DCR
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("https://app.test/cb#fragment")]
    [InlineData("http://not-loopback.test/cb")]
    [InlineData("/relative/path")]
    public async Task AdminCreate_RefusesRedirectUrisTheDcrEndpointWouldRefuse(string redirectUri)
    {
        // The two registration paths disagreed about what a valid redirect URI is, and the PRIVILEGED
        // one was the permissive one: DCR required an absolute URI, no fragment, https outside
        // loopback and no script pseudo-scheme, while the admin API wrote whatever it was given.
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { redirectUri },
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminCreate_StillAcceptsALegitimateRedirectUri()
    {
        // Native custom schemes (mobile deep links) and loopback http must keep working — RFC 8252
        // §7.3 requires the latter.
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb", "com.example.app:/oauth", "http://127.0.0.1:7890/cb" },
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task AdminCreate_ChecksPostLogoutRedirectUrisToo()
    {
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            postLogoutRedirectUris = new[] { "javascript:alert(1)" },
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Every client write announces itself, so the pooled CORS origin list does not stay stale.
    /// </summary>
    /// <remarks>
    /// Disabling a compromised client used to leave its origin credentialed on the protocol surface for
    /// up to <c>Cache:CorsCacheMinutes</c> (60) on every node with a warm entry, and nothing told the
    /// operator that their revocation had not taken effect.
    /// </remarks>
    [Fact]
    public async Task ClientWrites_PublishACorsInvalidation()
    {
        var clientId = $"c{Guid.NewGuid():N}";
        await CreateAsync(new
        {
            clientId,
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            allowedCorsOrigins = new[] { "https://app.test" },
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });
        Assert.Contains(DynamicCorsPolicyProvider.InvalidationTopic, _host.ClusterEvents.Published);

        _host.ClusterEvents.Published.Clear();
        await _client.PutAsync($"/api/v1/clients/{clientId}", Json(new
        {
            clientId,
            clientName = "Test",
            enabled = false,
            redirectUris = new[] { "https://app.test/cb" },
            allowedCorsOrigins = new[] { "https://app.test" },
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        }));
        Assert.Contains(DynamicCorsPolicyProvider.InvalidationTopic, _host.ClusterEvents.Published);

        _host.ClusterEvents.Published.Clear();
        await _client.DeleteAsync($"/api/v1/clients/{clientId}");
        Assert.Contains(DynamicCorsPolicyProvider.InvalidationTopic, _host.ClusterEvents.Published);
    }

    // -----------------------------------------------------------------------
    // #186 (re-verification) — the two logout URIs are dereferenced BY THE SERVER (back-channel is an
    // outbound POST from the logout path, front-channel is rendered into an iframe). DCR ran them
    // through the outbound-URL guard; the admin API ran nothing, so the privileged surface was the
    // permissive one and an IdentityAdmin — or a stolen admin token — could aim either at the cloud
    // metadata address or an internal host and trigger the fetch by logging out.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:8080/logout")]
    [InlineData("https://10.1.2.3/logout")]
    [InlineData("https://vault.internal/logout")]
    [InlineData("/relative/logout")]
    public async Task AdminCreate_RefusesInternalBackChannelLogoutUri(string logoutUri)
    {
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            backChannelLogoutUri = logoutUri,
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminCreate_RefusesInternalFrontChannelLogoutUri()
    {
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            frontChannelLogoutUri = "http://169.254.169.254/latest/meta-data/",
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Update is the other write path, and it is the one an attacker uses on an existing client.</summary>
    [Fact]
    public async Task AdminUpdate_RefusesInternalBackChannelLogoutUri()
    {
        var clientId = $"c{Guid.NewGuid():N}";
        var created = await CreateAsync(new
        {
            clientId,
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            backChannelLogoutUri = "https://app.test/backchannel",
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await _client.PutAsync($"/api/v1/clients/{clientId}", Json(new
        {
            clientId,
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            backChannelLogoutUri = "http://169.254.169.254/latest/meta-data/",
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await _host.ClientStore.GetAsync(clientId);
        Assert.Equal("https://app.test/backchannel", stored!.BackChannelLogoutUri);
    }

    /// <summary>The control: a real external https logout endpoint is exactly the supported case.</summary>
    [Fact]
    public async Task AdminCreate_AcceptsExternalLogoutUris()
    {
        var response = await CreateAsync(new
        {
            clientId = $"c{Guid.NewGuid():N}",
            clientName = "Test",
            redirectUris = new[] { "https://app.test/cb" },
            backChannelLogoutUri = "https://app.test/oidc/backchannel",
            frontChannelLogoutUri = "https://app.test/oidc/frontchannel",
            allowedScopes = new[] { "openid" },
            allowedGrantTypes = new[] { "authorization_code" },
        });

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // -----------------------------------------------------------------------
    // #14 (re-verification) — RFC 6749 §4.4 restricts client_credentials to confidential clients.
    // The token endpoint refuses the combination at runtime; nothing stopped an operator creating a
    // client in that state and discovering it at the first machine-to-machine call.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateClient_PublicWithClientCredentials_IsRefused()
    {
        var response = await CreateAsync(new
        {
            clientId = "public-cc",
            clientName = "Public with client_credentials",
            requireClientSecret = false,
            allowedGrantTypes = new[] { "client_credentials" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("confidential", json.GetProperty("error_description").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The control: the same grant on a confidential client is exactly the supported case.</summary>
    [Fact]
    public async Task CreateClient_ConfidentialWithClientCredentials_IsPermitted()
    {
        var response = await CreateAsync(new
        {
            clientId = "confidential-cc",
            clientName = "Confidential with client_credentials",
            requireClientSecret = true,
            allowedGrantTypes = new[] { "client_credentials" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateClient_Valid_Returns201WithLocation()
    {
        var response = await CreateAsync(new
        {
            clientId = "app-1",
            clientName = "App One",
            allowedScopes = new[] { "openid", "profile" },
            allowedGrantTypes = new[] { "authorization_code" },
            redirectUris = new[] { "https://app.example/callback" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/clients/app-1", response.Headers.Location?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app-1", json.GetProperty("clientId").GetString());
        Assert.Equal("App One", json.GetProperty("clientName").GetString());

        var stored = await _host.ClientStore.GetAsync("app-1");
        Assert.NotNull(stored);
        Assert.Equal(["openid", "profile"], stored!.AllowedScopes);
    }

    [Fact]
    public async Task CreateClient_SecretHashes_StoredButNeverEchoed()
    {
        var response = await CreateAsync(new
        {
            clientId = "secret-app",
            clientName = "Secret App",
            clientSecretHashes = new[] { SampleHash },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("clientSecretHashes").GetArrayLength()); // masked in the response

        var stored = await _host.ClientStore.GetAsync("secret-app");
        Assert.Equal([SampleHash], stored!.ClientSecretHashes); // but persisted intact
    }

    [Fact]
    public async Task CreateClient_MissingClientIdOrName_Returns400()
    {
        var noId = await CreateAsync(new { clientName = "No Id" });
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        var json = await noId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());

        var noName = await CreateAsync(new { clientId = "no-name" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }

    [Fact]
    public async Task CreateClient_AdminScope_Returns403ForbiddenScope()
    {
        var response = await CreateAsync(new
        {
            clientId = "evil",
            clientName = "Evil",
            allowedScopes = new[] { "openid", "authagonal-admin" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden_scope", json.GetProperty("error").GetString());
        Assert.Null(await _host.ClientStore.GetAsync("evil"));
    }

    [Fact]
    public async Task CreateClient_AdminScope_CaseInsensitive_Returns403()
    {
        var response = await CreateAsync(new
        {
            clientId = "evil-caps",
            clientName = "Evil Caps",
            allowedScopes = new[] { "AUTHAGONAL-ADMIN" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateClient_Duplicate_Returns409()
    {
        await CreateAsync(new { clientId = "dupe", clientName = "First" });
        var response = await CreateAsync(new { clientId = "dupe", clientName = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client_exists", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://app.example/home")] // http only allowed for loopback
    [InlineData("//app.example/home")]
    public async Task CreateClient_InvalidHomeUri_Returns400(string clientUri)
    {
        var response = await CreateAsync(new
        {
            clientId = "bad-uri",
            clientName = "Bad Uri",
            clientUri,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateClient_LoopbackHttpHomeUri_Allowed()
    {
        var response = await CreateAsync(new
        {
            clientId = "local-app",
            clientName = "Local App",
            clientUri = "http://localhost:3000/",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateClient_DefaultApplication_ClearsFlagFromOthers()
    {
        await CreateAsync(new { clientId = "d1", clientName = "D1", isDefaultApplication = true });
        await CreateAsync(new { clientId = "d2", clientName = "D2", isDefaultApplication = true });

        Assert.False((await _host.ClientStore.GetAsync("d1"))!.IsDefaultApplication);
        Assert.True((await _host.ClientStore.GetAsync("d2"))!.IsDefaultApplication);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/clients + /{clientId}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListClients_RedactsSecretHashes()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "listed",
            ClientName = "Listed",
            ClientSecretHashes = ["h1", "h2"],
        });

        var response = await _client.GetAsync("/api/v1/clients/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var listed = json.EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "listed");
        Assert.Equal(0, listed.GetProperty("clientSecretHashes").GetArrayLength());

        // Redaction must not mutate the cached/stored instance
        Assert.Equal(2, (await _host.ClientStore.GetAsync("listed"))!.ClientSecretHashes.Count);
    }

    [Fact]
    public async Task GetClient_ReturnsRedactedClient()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "gettable",
            ClientName = "Gettable",
            ClientSecretHashes = ["h1"],
        });

        var response = await _client.GetAsync("/api/v1/clients/gettable");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Gettable", json.GetProperty("clientName").GetString());
        Assert.Equal(0, json.GetProperty("clientSecretHashes").GetArrayLength());
    }

    [Fact]
    public async Task GetClient_Unknown_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/clients/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // PUT /api/v1/clients/{clientId} — UpdateClient
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpdateClient_Unknown_Returns404()
    {
        var response = await _client.PutAsync("/api/v1/clients/missing",
            Json(new { clientId = "missing", clientName = "Missing" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateClient_OmittedSecretHashes_PreservesStoredSecret()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "keeper",
            ClientName = "Keeper",
            ClientSecretHashes = ["original-hash"],
        });

        var response = await _client.PutAsync("/api/v1/clients/keeper",
            Json(new { clientId = "keeper", clientName = "Keeper v2" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Keeper v2", json.GetProperty("clientName").GetString());
        Assert.Equal(0, json.GetProperty("clientSecretHashes").GetArrayLength()); // still masked

        var stored = await _host.ClientStore.GetAsync("keeper");
        Assert.Equal(["original-hash"], stored!.ClientSecretHashes);
        Assert.Equal("Keeper v2", stored.ClientName);
    }

    [Fact]
    public async Task UpdateClient_ExplicitSecretHashes_RotatesSecret()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "rotator",
            ClientName = "Rotator",
            ClientSecretHashes = [SampleHash],
        });

        var rotated = CheapHasher.Password().HashPassword("rotated-secret");
        var response = await _client.PutAsync("/api/v1/clients/rotator",
            Json(new { clientId = "rotator", clientName = "Rotator", clientSecretHashes = new[] { rotated } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([rotated], (await _host.ClientStore.GetAsync("rotator"))!.ClientSecretHashes);
    }

    /// <summary>A hash this server would actually have written.</summary>
    private static readonly string SampleHash = CheapHasher.Password().HashPassword("a-real-secret");

    [Fact]
    public async Task CreateClient_UnrecognisedSecretHashFormat_Returns400()
    {
        // A stored hash tells this server how much CPU to spend on the next anonymous /connect/token
        // call for the client. An arbitrary blob fell through to the ASP.NET Identity parser, where
        // the iteration count came from the blob itself — so an admin could plant a hash declaring
        // 2^31-1 iterations and then trigger hours of uncancellable PBKDF2 anonymously.
        var response = await CreateAsync(new
        {
            clientId = "poisoned",
            clientName = "Poisoned",
            clientSecretHashes = new[] { "AQAAAAEAmJaAAAAAEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await _host.ClientStore.GetAsync("poisoned"));
    }

    [Fact]
    public async Task CreateClient_EmptySecretHashEntry_Returns400()
    {
        // VerifyPassword throws on an empty hash, which turned a [""] entry into an unhandled 500 on
        // every token request for that client.
        var response = await CreateAsync(new
        {
            clientId = "empty-hash",
            clientName = "Empty Hash",
            clientSecretHashes = new[] { "" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateClient_AdminScope_Returns403ForbiddenScope()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "escalate",
            ClientName = "Escalate",
            AllowedScopes = ["openid"],
        });

        var response = await _client.PutAsync("/api/v1/clients/escalate",
            Json(new { clientId = "escalate", clientName = "Escalate", allowedScopes = new[] { "openid", "authagonal-admin" } }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden_scope", json.GetProperty("error").GetString());
        Assert.Equal(["openid"], (await _host.ClientStore.GetAsync("escalate"))!.AllowedScopes);
    }

    [Fact]
    public async Task UpdateClient_RouteClientIdWins_OverBodyClientId()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient { ClientId = "route-id", ClientName = "Old" });

        var response = await _client.PutAsync("/api/v1/clients/route-id",
            Json(new { clientId = "different-id", clientName = "Renamed" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await _host.ClientStore.GetAsync("route-id"))!.ClientName);
        Assert.Null(await _host.ClientStore.GetAsync("different-id"));
    }

    [Fact]
    public async Task UpdateClient_InvalidHomeUri_Returns400()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient { ClientId = "upd-uri", ClientName = "U" });

        var response = await _client.PutAsync("/api/v1/clients/upd-uri",
            Json(new { clientId = "upd-uri", clientName = "U", initiateLoginUri = "ftp://files.example/x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // DELETE /api/v1/clients/{clientId}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteClient_Existing_Returns204AndRemoves()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient { ClientId = "goner", ClientName = "Goner" });

        var response = await _client.DeleteAsync("/api/v1/clients/goner");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await _client.GetAsync("/api/v1/clients/goner");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DeleteClient_Unknown_Returns404()
    {
        var response = await _client.DeleteAsync("/api/v1/clients/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Unauthenticated callers rejected
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/v1/clients/")]
    [InlineData("GET", "/api/v1/clients/some-client")]
    [InlineData("POST", "/api/v1/clients/")]
    [InlineData("PUT", "/api/v1/clients/some-client")]
    [InlineData("DELETE", "/api/v1/clients/some-client")]
    public async Task ClientEndpoints_NoAuth_Returns401(string method, string url)
    {
        var anonymous = _host.CreateClient(authenticated: false);
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
            request.Content = Json(new { clientId = "x", clientName = "X" });

        var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// ---------------------------------------------------------------------------------------------
// Client admin read endpoints + auth rejection through the REAL JWT-bearer pipeline
// (AuthagonalTestFactory: token minted via client_credentials at /connect/token).
// ---------------------------------------------------------------------------------------------
public sealed class AdminClientEndpointTests_RealJwt : IAsyncLifetime
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

    [Fact]
    public async Task ListClients_WithRealAdminJwt_ReturnsSeededClientsRedacted()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var admin = json.EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == AuthagonalTestFactory.AdminClientId);
        // The seeded admin client has a real bcrypt secret hash in the store — the API must not leak it.
        Assert.Equal(0, admin.GetProperty("clientSecretHashes").GetArrayLength());
    }

    [Fact]
    public async Task GetClient_WithRealAdminJwt_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/clients/{AuthagonalTestFactory.TestClientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthagonalTestFactory.TestClientId, json.GetProperty("clientId").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/v1/clients/")]
    [InlineData("GET", "/api/v1/clients/test-client")]
    [InlineData("POST", "/api/v1/clients/")]
    [InlineData("PUT", "/api/v1/clients/test-client")]
    [InlineData("DELETE", "/api/v1/clients/test-client")]
    public async Task ClientEndpoints_NoToken_Returns401(string method, string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClientEndpoints_GarbageToken_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
