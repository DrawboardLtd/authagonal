using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
using Authagonal.Server;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Endpoints.Scim;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;
using Authagonal.Server.Services.Oidc;
using Authagonal.Server.Services.Saml;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Test server that CALLS the real registration — <c>AddAuthagonalCore</c>, <c>UseAuthagonal</c>,
/// <c>MapAuthagonalEndpoints</c> — with in-memory stores and test doubles pre-registered ahead of it.
/// </summary>
/// <remarks>
/// It used to MIRROR <c>AddAuthagonal</c>: some three hundred lines restating the options, the
/// authentication schemes, the authorization policies, the rate limiter, the named outbound clients and the
/// CORS wiring. That made every hardening added to the real registration invisible here until someone
/// thought to repeat it — so a fix could land in production, the suite could stay green, and these tests
/// were exercising the unfixed configuration. It is how a <c>typ</c>-validation test came to pass against
/// unfixed code, how the tenant-scoping rate-limit decorator came to be exercised by nothing, and how five
/// further divergences reached a pushed branch.
/// <para>
/// The measure of the difference: changing <c>ValidAlgorithms</c> in <c>AddAuthagonalCore</c> now fails 168
/// tests. Against the mirrored factory it failed one — the comparison test that existed to notice, because
/// the factory declared its own <c>ES256</c> and the test host kept validating exactly as before.
/// </para>
/// <para>
/// <b>The contract, if you are adding to this file.</b> Registrations that replace a production default go
/// ABOVE the <c>AddAuthagonalCore</c> call: it reaches its extensibility points through <c>TryAdd</c> and
/// <c>if (!services.Any(T))</c>, so whatever lands first wins and the production default never appears.
/// Anything placed below the call silently loses to that default instead. Only a deliberate deviation —
/// something a test host cannot have — belongs below, and each one there says why.
/// </para>
/// </remarks>
public sealed class AuthagonalTestFactory : IAsyncDisposable
{
    public const string TestIssuer = "https://test.authagonal.local";
    public const string TestClientId = "test-client";
    public const string TestClientSecret = "test-secret-123";
    public const string AdminClientId = "admin-client";
    public const string AdminClientSecret = "admin-secret-456";
    public const string AdminScope = "authagonal-admin";

    public InMemoryUserStore UserStore { get; } = new();
    public InMemoryClientStore ClientStore { get; } = new();
    public InMemoryGrantStore GrantStore { get; } = new();
    public InMemorySigningKeyStore SigningKeyStore { get; } = new();
    public InMemorySsoDomainStore SsoDomainStore { get; } = new();
    public InMemorySamlProviderStore SamlProviderStore { get; } = new();
    public InMemoryOidcProviderStore OidcProviderStore { get; } = new();
    public InMemoryUserProvisionStore UserProvisionStore { get; } = new();
    public InMemoryMfaStore MfaStore { get; } = new();
    public InMemoryScimTokenStore ScimTokenStore { get; } = new();
    public InMemoryScimGroupStore ScimGroupStore { get; } = new();
    public WritableScimGroupRoleMappingStore ScimGroupRoleMappingStore { get; } = new();
    public InMemoryRoleStore RoleStore { get; } = new();
    public InMemoryScopeStore ScopeStore { get; } = new();
    public InMemoryRevokedTokenStore RevokedTokenStore { get; } = new();
    public InMemoryAgentProfileStore AgentProfileStore { get; } = new();
    public TestEmailService EmailService { get; } = new();
    public TestAuthHook AuthHook { get; } = new();
    public RecordingAuditLogger AuditLog { get; } = new();

    /// <summary>Every log record the host wrote — for asserting that a secret is absent from it.</summary>
    public RecordingLoggerProvider LogSink { get; } = new();
    public TestTokenExchangeSubjectTransformer ExchangeTransformer { get; } = new();
    public TestProvisioningOrchestrator Provisioning { get; } = new();

    /// <summary>Set before starting the factory to inject a mock HTTP handler for OIDC/SAML metadata.</summary>
    public HttpMessageHandler? OidcHttpHandler { get; set; }
    public HttpMessageHandler? SamlHttpHandler { get; set; }

    /// <summary>Backs the "AuthagonalJwks" named client — the client jwks_uri fetch on the private_key_jwt path.</summary>
    public HttpMessageHandler? JwksHttpHandler { get; set; }

    /// <summary>
    /// Set before starting the factory to intercept the logout-token fan-out instead of doing real
    /// socket I/O. Needed because the sender now applies the outbound SSRF guard, so a loopback capture
    /// listener — the only address a test can bind — is refused before any request is made.
    /// </summary>
    public HttpMessageHandler? BackChannelLogoutHttpHandler { get; set; }

    /// <summary>Set to an Azurite connection string to enable SAML/OIDC state storage.</summary>
    public string? AzuriteConnectionString { get; set; }

    /// <summary>Optional mutator applied to AuthOptions at DI configuration time.</summary>
    public Action<AuthOptions>? ConfigureAuthOptions { get; set; }

    /// <summary>
    /// Configuration keys applied to the host before it starts, for settings read straight from
    /// <c>IConfiguration</c> rather than from an options class — <c>ForwardedHeaders:*</c>, for one,
    /// which <c>UseAuthagonal</c> reads at pipeline-composition time.
    /// </summary>
    public Dictionary<string, string?> Configuration { get; } = [];

    private WebApplication? _app;
    private bool _started;

    public IServiceProvider Services => EnsureStarted()._app!.Services;

    public HttpClient CreateClient(WebApplicationFactoryClientOptions? options = null)
    {
        EnsureStarted();
        var testServer = _app!.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");

        if (options is { AllowAutoRedirect: false })
        {
            // No redirect following but still maintain cookies between requests
            var handler = testServer.CreateHandler();
            var cookieHandler = new CookieHandler(handler);
            return new HttpClient(cookieHandler) { BaseAddress = testServer.BaseAddress };
        }

        // Default: use TestServer's built-in client which follows redirects and maintains cookies
        return testServer.CreateClient();
    }

    /// <summary>Seed test data: a PKCE client and an admin client with a known secret.</summary>
    public async Task SeedTestDataAsync()
    {
        EnsureStarted();
        var passwordHasher = Services.GetRequiredService<PasswordHasher>();

        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = TestClientId,
            ClientName = "Test SPA",
            RequireClientSecret = false,
            RequirePkce = true,
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = ["https://app.test/callback"],
            PostLogoutRedirectUris = ["https://app.test"],
            AllowedScopes = ["openid", "profile", "email", "offline_access"],
            AllowOfflineAccess = true,
            AccessTokenLifetimeSeconds = 3600,
            Audiences = ["https://api.test/v1", "https://api.test/v2"],
        });

        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = AdminClientId,
            ClientName = "Admin Client",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [passwordHasher.HashPassword(AdminClientSecret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", AdminScope],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    /// <summary>Create a confirmed test user and return their ID.</summary>
    public async Task<AuthUser> SeedTestUserAsync(
        string email = "test@example.com",
        string password = "Test1234!",
        bool emailConfirmed = true)
    {
        EnsureStarted();
        var passwordHasher = Services.GetRequiredService<PasswordHasher>();
        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = passwordHasher.HashPassword(password),
            EmailConfirmed = emailConfirmed,
            FirstName = "Test",
            LastName = "User",
            LockoutEnabled = true,
            SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await UserStore.CreateAsync(user);
        return user;
    }

    /// <summary>Seed a SCIM client and return the raw Bearer token.</summary>
    public async Task<(string ClientId, string RawToken)> SeedScimClientAsync(
        string clientId = "scim-client",
        string? orgId = null)
    {
        EnsureStarted();

        // Create the client
        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "SCIM Client",
            RequireClientSecret = false,
            AllowedGrantTypes = [],
            AllowedScopes = [],
        });

        // Generate and store a SCIM token
        var rawTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawTokenBytes);
        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

        var scimToken = new ScimToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            TokenHash = tokenHash,
            Description = "Test SCIM token",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await ScimTokenStore.StoreAsync(scimToken);

        return (clientId, rawToken);
    }

    /// <summary>Get an admin bearer token via client_credentials grant.</summary>
    public async Task<string> GetAdminTokenAsync(HttpClient? client = null)
    {
        client ??= CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AdminScope
        });
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AdminClientId}:{AdminClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private AuthagonalTestFactory EnsureStarted()
    {
        if (_started) return this;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Issuer"] = TestIssuer;
        builder.Configuration["Oidc:Issuer"] = TestIssuer;
        builder.Configuration["AdminApi:Enabled"] = "true";
        builder.Configuration["Cluster:Enabled"] = "false";

        // Two of the four deliberate deviations from production are already CONFIGURATION on the real
        // registration, so they need no divergent code at all — which is the whole point of calling
        // AddAuthagonalCore instead of reimplementing it.
        //
        // TestServer speaks plain http (BaseAddress is http://localhost), so without these the TLS gate
        // over /connect/* would refuse every protocol request and CookieContainer would refuse to send a
        // Secure __Host- cookie over http, breaking every cookie-dependent test. Set as configuration keys
        // rather than by post-configuring options, because that is how an operator sets them and because
        // Auth:AllowInsecureHttp has to reach AuthagonalProtocolOptions too — AddAuthagonalCore propagates
        // it, and a test host that set the options object directly would leave /connect/par answering
        // differently from the rest of the surface.
        builder.Configuration["Auth:AllowInsecureHttp"] = "true";
        builder.Configuration["Authentication:AllowInsecureCookie"] = "true";

        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(LogSink);

        // Applied last so a test can override any of the above.
        foreach (var (key, value) in Configuration)
            builder.Configuration[key] = value;

        var services = builder.Services;

        // ---------------------------------------------------------------------------
        // Everything below, up to the AddAuthagonalCore call, exists to be registered BEFORE it.
        //
        // AddAuthagonalCore reaches its extensibility points through TryAdd and through
        // `if (!services.Any(T))` gates, so a registration that lands first WINS and the production default
        // never appears. That is the documented contract ("register your implementations before calling
        // this method"), and it is what lets this factory swap storage and doubles without reimplementing
        // the container. Anything moved below the call silently loses to production's default instead.
        // ---------------------------------------------------------------------------

        // In-memory stores (replacing Azure Table Storage). These also skip AddAuthagonal's storage block
        // entirely — it is gated on `!services.Any(IUserStore)` — which is why this host needs no
        // connection string and no Azurite.
        services.AddSingleton<IUserStore>(UserStore);
        services.AddSingleton<IClientStore>(ClientStore);
        services.AddSingleton<IGrantStore>(GrantStore);
        services.AddSingleton<ISigningKeyStore>(SigningKeyStore);
        services.AddSingleton<ISsoDomainStore>(SsoDomainStore);
        services.AddSingleton<ISamlProviderStore>(SamlProviderStore);
        services.AddSingleton<IOidcProviderStore>(OidcProviderStore);
        services.AddSingleton<IUserProvisionStore>(UserProvisionStore);
        services.AddSingleton<IMfaStore>(MfaStore);
        services.AddSingleton<IScimTokenStore>(ScimTokenStore);
        services.AddSingleton<IScimGroupStore>(ScimGroupStore);
        services.AddSingleton<IScimGroupRoleMappingStore>(ScimGroupRoleMappingStore);
        services.AddSingleton<IRoleStore>(RoleStore);
        services.AddSingleton<IScopeStore>(ScopeStore);
        services.AddSingleton<IRevokedTokenStore>(RevokedTokenStore);
        services.AddSingleton<IAgentProfileStore>(AgentProfileStore);

        // Tenant context
        services.AddSingleton<Authagonal.Core.Services.ITenantContext>(
            new TestTenantContext(TestIssuer));

        // Extensibility test doubles. Every one of these is a TryAdd or a gate in the real registration.
        services.AddSingleton<IEmailService>(EmailService);
        services.AddSingleton<IAuthHook>(AuthHook);
        // AddAuthagonalCore TryAddSingletons NullAuditLogger; the suite records instead, so an admin write
        // can be asserted to leave an attributable trail.
        services.AddSingleton<IAuditLogger>(AuditLog);
        services.AddSingleton<Authagonal.Protocol.ITokenExchangeSubjectTransformer>(ExchangeTransformer);
        services.AddSingleton<IProvisioningOrchestrator>(Provisioning);
        services.AddSingleton<ISecretProvider>(new PlaintextSecretProvider());

        // DataProtection: AddAuthagonal attaches a durable, encrypted key ring keyed off the storage
        // configuration this host does not have, so the ephemeral default is what a test host gets. Called
        // here rather than left implicit because the cookie handler needs it.
        services.AddDataProtection();

        // ---------------------------------------------------------------------------
        // THE REAL REGISTRATION.
        //
        // This factory used to MIRROR it — around 300 lines restating the options, the authentication
        // schemes, the authorization policies, the rate limiter, the named outbound clients and the CORS
        // wiring. Anything hardened in the real registration was then invisible to every test using this
        // factory until someone thought to repeat it here, so a fix could land in production, the suite
        // could stay green, and the tests were exercising the unfixed configuration. That is not
        // hypothetical: it is how a `typ`-validation test came to pass against unfixed code, how the
        // tenant-scoping rate-limit decorator came to be exercised by nothing, and how five more
        // divergences reached a pushed branch.
        //
        // AddAuthagonalCore and not AddAuthagonal, deliberately. What AddAuthagonal adds on top is storage
        // (replaced above), the DataProtection key ring (no store here), background/seed hosted services
        // (a test host wants no timers), and the four registrations restated after this call. Every
        // divergence that has actually caused a miss — the bearer scheme, the cookie policy, the rate
        // limiter, the named clients, the outbound allowlist — lives in Core. TestFactoryMirrorsProduction
        // Tests asserts the remainder against a container built from the full AddAuthagonal.
        // ---------------------------------------------------------------------------
        services.AddAuthagonalCore(builder.Configuration);

        // ---------------------------------------------------------------------------
        // Deliberate deviations, applied AFTER the real registration so they override it. Each one is
        // here because a test host cannot have what production has; nothing else belongs in this section.
        // ---------------------------------------------------------------------------

        // 1. PBKDF2 cost.
        //
        // Production refuses to start below AuthOptions.MinimumPbkdf2Iterations (100,000) and validates it
        // on start, which is right: a deployment quietly writing weaker hashes than its operator believes
        // is worth failing for. But at any conforming cost the thousands of sign-ins in this suite spend
        // minutes in the KDF and measure nothing but the KDF, so the floor's VALIDATOR is removed and the
        // cost set below it. Removing the validator is the honest way to do that — the alternative is
        // configuring 1,000 and having the host refuse to start, or leaving the suite unusably slow.
        // Tests that care about the cost (format, recorded iterations, rehash-on-login) set it explicitly.
        services.RemoveAll<IValidateOptions<AuthOptions>>();
        services.PostConfigure<AuthOptions>(o =>
        {
            o.Pbkdf2Iterations = 1_000;
            ConfigureAuthOptions?.Invoke(o);
        });

        // Keeps the protocol surface in step with a test that turns AllowInsecureHttp back OFF through
        // ConfigureAuthOptions rather than through configuration. AddAuthagonalCore propagates the
        // CONFIGURATION value to AuthagonalProtocolOptions, which cannot see a later mutation of
        // AuthOptions — and /connect/par carries the protocol's own TLS filter, so without this bridge that
        // one endpoint would answer differently from the rest of the surface.
        services.AddSingleton<IPostConfigureOptions<AuthagonalProtocolOptions>>(sp =>
            new PostConfigureOptions<AuthagonalProtocolOptions>(Options.DefaultName, o =>
                o.AllowInsecureHttp = sp.GetRequiredService<IOptions<AuthOptions>>().Value.AllowInsecureHttp));

        // 2. Test handlers on the named outbound clients.
        //
        // ConfigurePrimaryHttpMessageHandler LAST wins, so this replaces only the primary handler and keeps
        // the production timeout and redirect policy for the name. Registering the whole client here
        // instead would restate that policy and drift from it, which is what findings #52 / #62 / #66 /
        // #346 exist to enforce and what this factory previously got wrong.
        foreach (var (name, handler) in new (string, HttpMessageHandler?)[]
                 {
                     ("SamlMetadata", SamlHttpHandler),
                     ("OidcDiscovery", OidcHttpHandler),
                     ("AuthagonalJwks", JwksHttpHandler),
                     ("BackChannelLogout", BackChannelLogoutHttpHandler),
                 })
        {
            if (handler is not null)
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        // 3. The four things AddAuthagonal adds that this host still needs, restated because the rest of
        // that method is storage and hosted services.

        // The bearer scheme's signing-key resolver.
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp));

        // SAML replay cache + OIDC state store. Production wires these from the Azure keyed TableClients;
        // registered here AFTER AddAuthagonalCore on purpose, so nothing gates on their presence — in
        // particular OidcStateSweepService, a background timer this host does not want.
        var stateConnStr = AzuriteConnectionString ?? "UseDevelopmentStorage=true";
        var samlTable = new TableClient(stateConnStr, $"SamlReplay{Guid.NewGuid():N}"[..20]);
        var oidcTable = new TableClient(stateConnStr, $"OidcState{Guid.NewGuid():N}"[..20]);
        if (AzuriteConnectionString is not null)
        {
            samlTable.CreateIfNotExists();
            oidcTable.CreateIfNotExists();
        }
        services.AddKeyedSingleton("SamlReplayCache", samlTable);
        services.AddKeyedSingleton("OidcStateStore", oidcTable);
        services.AddSingleton<SamlReplayCache>(sp =>
            new SamlReplayCache(sp.GetRequiredKeyedService<TableClient>("SamlReplayCache"), sp.GetRequiredService<IOptions<CacheOptions>>()));
        services.AddSingleton<OidcStateStore>(sp =>
            new OidcStateStore(sp.GetRequiredKeyedService<TableClient>("OidcStateStore"), sp.GetRequiredService<IOptions<CacheOptions>>()));
        services.AddSingleton<Authagonal.Core.Services.ISamlReplayCache>(sp => sp.GetRequiredService<SamlReplayCache>());
        services.AddSingleton<Authagonal.Core.Services.IOidcStateStore>(sp => sp.GetRequiredService<OidcStateStore>());

        // The storage health check.
        services.TryAddSingleton<TableStorageHealthCheck>();
        services.AddHealthChecks().AddCheck<TableStorageHealthCheck>("table_storage");

        _app = builder.Build();

        // Pipeline (mirrors UseAuthagonal + MapAuthagonalEndpoints)
        _app.UseAuthagonal();
        _app.MapAuthagonalEndpoints();

        // SCIM endpoints are wired via MapAuthagonalEndpoints already

        _app.StartAsync().GetAwaiter().GetResult();
        _started = true;

        return this;
    }

    /// <summary>Handler that maintains cookies between requests but does NOT follow redirects.</summary>
    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Add stored cookies to the request
            var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.Add("Cookie", cookieHeader);

            var response = await base.SendAsync(request, cancellationToken);

            // Store cookies from the response
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var setCookie in setCookieHeaders)
                    _cookies.SetCookies(request.RequestUri!, setCookie);
            }

            return response;
        }
    }
}

/// <summary>Client options for test factory.</summary>
public sealed class WebApplicationFactoryClientOptions
{
    public bool AllowAutoRedirect { get; set; } = true;
}
