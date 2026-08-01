using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
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
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Test server that mirrors AddAuthagonal/UseAuthagonal/MapAuthagonalEndpoints
/// but uses in-memory stores instead of Azure Table Storage.
/// </summary>
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
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(LogSink);

        foreach (var (key, value) in Configuration)
            builder.Configuration[key] = value;

        var services = builder.Services;

        // In-memory stores (replacing Azure Table Storage)
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

        // Rate limiter — through the SAME tenant-scoping decorator the real registration installs.
        //
        // This used to register a bare InProcessRateLimiter as IRateLimiter, so TenantScopedRateLimiter was
        // exercised by no test in the suite: every key reached the limiter WITHOUT its tenant prefix, which
        // is precisely the cross-tenant denial of service the decorator exists to prevent. The registration
        // ORDER that makes the decorator win in the real host was treated as load-bearing during the branch
        // merge and verified only by reading, because nothing here could have caught getting it wrong.
        services.AddSingleton<Authagonal.Core.Services.InProcessRateLimiter>();
        services.AddSingleton<IRateLimiter>(sp => new TenantScopedRateLimiter(
            sp.GetRequiredService<Authagonal.Core.Services.InProcessRateLimiter>(),
            sp.GetRequiredService<IHttpContextAccessor>()));

        // Extensibility test doubles
        services.AddSingleton<IEmailService>(EmailService);
        services.AddSingleton<IAuthHook>(AuthHook);
        // AddAuthagonal TryAddSingletons NullAuditLogger; the suite records instead, so an admin write
        // can be asserted to leave an attributable trail.
        services.AddSingleton<IAuditLogger>(AuditLog);
        services.AddSingleton<Authagonal.Protocol.ITokenExchangeSubjectTransformer>(ExchangeTransformer);
        services.AddSingleton<IProvisioningOrchestrator>(Provisioning);
        services.AddSingleton<ISecretProvider>(new PlaintextSecretProvider());

        // Options (mirrors AddAuthagonal)
        services.Configure<AuthOptions>(o =>
        {
            // Deliberately far below AuthOptions.MinimumPbkdf2Iterations. The production floor is
            // enforced where configuration is bound, not by the hasher, precisely so the suite can
            // opt out: at the real 600,000 the thousands of sign-ins across these tests spend
            // minutes in PBKDF2 and measure nothing but the KDF. Tests that care about the cost
            // (format, recorded iterations, rehash-on-login) set it explicitly.
            o.Pbkdf2Iterations = 1_000;
            // TestServer speaks plain http (its BaseAddress is http://localhost), so the TLS gate
            // UseAuthagonal installs over /connect/* would refuse every protocol request here.
            o.AllowInsecureHttp = true;
            // Both set BEFORE the mutator, so a test can turn either back off — TransportSecurityTests
            // does exactly that with AllowInsecureHttp.
            ConfigureAuthOptions?.Invoke(o);
        });
        services.Configure<CacheOptions>(_ => { });
        services.Configure<BackgroundServiceOptions>(_ => { });

        // Core services (mirrors AddAuthagonal minus storage)
        services.AddHttpContextAccessor();
        services.AddLocalization();
        services.AddDataProtection();
        services.AddSingleton(new PasswordPolicy());
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordValidator>();
        // Turnstile is opt-in; left unconfigured here so it's disabled (no token required).
        // Must still be registered so the /login + /register handlers' TurnstileVerifier
        // parameter resolves as a service rather than being inferred as a body param.
        services.Configure<TurnstileOptions>(_ => { });
        services.AddHttpClient<TurnstileVerifier>();

        // Protocol wiring — map AuthOptions onto AuthagonalProtocolOptions, plug in the
        // PasswordHasher-backed secret verifier so bcrypt/pbkdf2 client secrets verify
        // through the same pipeline as user passwords, then call AddAuthagonalProtocol.
        services.AddSingleton<IConfigureOptions<AuthagonalProtocolOptions>>(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            return new ConfigureNamedOptions<AuthagonalProtocolOptions>(Options.DefaultName, o =>
            {
                o.SigningKeyLifetimeDays = auth.SigningKeyLifetimeDays;
                o.SigningKeyCacheRefreshMinutes = auth.SigningKeyCacheRefreshMinutes;
                o.RefreshTokenReuseGraceSeconds = auth.RefreshTokenReuseGraceSeconds;
                o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Mirrors what AddAuthagonalCore does with Auth:AllowInsecureHttp. MapAuthagonalEndpoints
                // maps Protocol's PAR endpoint, which carries its own TLS filter, so a test that turns the
                // switch off has to turn it off for both or /connect/par would answer differently from the
                // rest of the surface.
                o.AllowInsecureHttp = auth.AllowInsecureHttp;
            });
        });
        services.AddSingleton<IClientSecretVerifier, PasswordHasherClientSecretVerifier>();
        services.AddAuthagonalProtocol(_ => { });
        services.AddScoped<UserStoreOidcSubjectResolver>();
        services.AddScoped<IOidcSubjectResolver>(sp => sp.GetRequiredService<UserStoreOidcSubjectResolver>());
        services.AddSingleton<TotpService>();
        services.AddSingleton<RecoveryCodeService>();
        services.AddSingleton<WebAuthnService>();
        services.AddFido2(options =>
        {
            options.ServerDomain = "test.authagonal.local";
            options.ServerName = "Authagonal Test";
            options.Origins = new HashSet<string> { TestIssuer };
        });
        // Named outbound clients, with the SAME handler policy the real registration applies: no redirect
        // following and a bounded timeout.
        //
        // These were registered bare, so every test involving an outbound fetch ran against a
        // redirect-following 100-second client while production refused redirects — which is the exact
        // property findings #52 / #62 / #66 / #346 exist to enforce, and it was unobservable from here. A
        // test handler still replaces the primary handler where one is supplied (that is the seam), but the
        // DEFAULT for each name now matches production instead of the framework default.
        //
        // Every name is registered unconditionally, handler or not. A CreateClient on an unregistered name
        // silently returns a default client, so a name missing here fails open in exactly the way the real
        // registration was found to fail open.
        foreach (var (name, handler) in new (string, HttpMessageHandler?)[]
                 {
                     ("Provisioning", null),
                     ("SamlMetadata", SamlHttpHandler),
                     ("OidcDiscovery", OidcHttpHandler),
                     ("AuthagonalJwks", JwksHttpHandler),
                     ("BackChannelLogout", BackChannelLogoutHttpHandler),
                     ("Resend", null),
                 })
        {
            var httpBuilder = services.AddHttpClient(name)
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));

            if (handler is not null)
                httpBuilder.ConfigurePrimaryHttpMessageHandler(() => handler);
            else
                httpBuilder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        }
        services.AddMemoryCache();

        // SAML/OIDC services (state stores need real table storage for SSO tests)
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
        services.AddSingleton<SamlMetadataParser>();
        services.AddSingleton<SamlResponseParser>();
        services.AddSingleton<SamlReplayCache>(sp =>
            new SamlReplayCache(sp.GetRequiredKeyedService<TableClient>("SamlReplayCache"), sp.GetRequiredService<IOptions<CacheOptions>>()));
        services.AddSingleton<OidcDiscoveryClient>();
        services.AddSingleton<OidcStateStore>(sp =>
            new OidcStateStore(sp.GetRequiredKeyedService<TableClient>("OidcStateStore"), sp.GetRequiredService<IOptions<CacheOptions>>()));
        // The SSO endpoint handlers inject the interface seams (ISamlReplayCache / IOidcStateStore),
        // which production wires via gated TryAdds keyed off the Azure TableClients. This bespoke test
        // host builds its own graph, so register the seams against the concretes above — without these,
        // minimal-API binding can't resolve the parameters and every /saml + /oidc/{conn}/login call 400s.
        services.AddSingleton<Authagonal.Core.Services.ISamlReplayCache>(sp => sp.GetRequiredService<SamlReplayCache>());
        services.AddSingleton<Authagonal.Core.Services.IOidcStateStore>(sp => sp.GetRequiredService<OidcStateStore>());

        // Authentication
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(48);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Diverges from production on purpose.
            //
            // AddAuthagonalCore defaults to CookieSecurePolicy.Always plus a __Host- cookie name.
            // TestServer speaks HTTP and CookieContainer refuses to send a Secure cookie over it, so
            // mirroring production here would break every cookie-dependent test in the suite.
            //
            // This factory therefore duplicates the production cookie wiring rather than calling into
            // it, and nothing about that wiring is exercised by a request made through this client.
            // The attributes themselves are asserted directly against AddAuthagonalCore, without any
            // HTTP, in CookiePolicyConfigurationTests — change them there too.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            options.Events.OnValidatePrincipal = async context =>
            {
                var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
                var userId = context.Principal?.FindFirst("sub")?.Value
                    ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userId is null || stampClaim is null) return;

                var lastValidated = context.Properties.GetString("stamp_validated");
                if (lastValidated is not null &&
                    DateTimeOffset.TryParse(lastValidated, out var lastTime) &&
                    DateTimeOffset.UtcNow - lastTime < TimeSpan.FromMinutes(30))
                    return;

                var userStore = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
                var user = await userStore.GetAsync(userId);
                if (user is null || !user.IsActive || !string.Equals(user.SecurityStamp ?? "", stampClaim ?? "", StringComparison.Ordinal))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                context.Properties.SetString("stamp_validated", DateTimeOffset.UtcNow.ToString("O"));
                context.ShouldRenew = true;
            };
        })
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TestIssuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                AudienceValidator = (audiences, _, _) => audiences?.Any() == true,
                ValidateLifetime = true,
                // RFC 9068 §4. Mirrored from AddAuthagonal, which this factory copies rather than
                // calls — so a hardening added to the real registration is invisible here until
                // someone repeats it. That divergence is exactly why the id_token-as-bearer defect
                // went unnoticed: production could have been fixed and every test using this factory
                // would still have exercised the unfixed configuration.
                ValidTypes = [Authagonal.Core.Constants.TokenTypes.AccessTokenJwt],
                // Pins the signing algorithm, as the real registration does — defence in depth against
                // algorithm confusion. Omitted here until the merge audit noticed, which meant every test
                // asserting on the resource-server scheme ran against a configuration that would accept a
                // token signed with an algorithm production refuses.
                ValidAlgorithms = ["ES256"],
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidateIssuerSigningKey = true
            };

            // Access-token revocation, as the real registration does: a token whose jti is in the revoked
            // store is refused even though it is still cryptographically valid and unexpired. Without this
            // the suite could not tell a working revocation path from a missing one on the bearer scheme.
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async ctx =>
                {
                    var jti = ctx.Principal?.FindFirst("jti")?.Value;
                    if (!string.IsNullOrEmpty(jti))
                    {
                        var revoked = ctx.HttpContext.RequestServices.GetRequiredService<IRevokedTokenStore>();
                        if (await revoked.IsRevokedAsync(jti, ctx.HttpContext.RequestAborted))
                            ctx.Fail("Token has been revoked");
                    }
                }
            };
        })
        .AddScheme<AuthenticationSchemeOptions, Authagonal.Server.Services.ScimBearerAuthenticationHandler>("ScimBearer", null);

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp));

        // Authorization
        var adminScope = builder.Configuration["AdminApi:Scope"] ?? "authagonal-admin";
        services.AddAuthorization(options =>
        {
            options.AddPolicy("IdentityAdmin", policy =>
            {
                policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var scopeClaim = context.User.FindFirst("scope") ?? context.User.FindFirst("scp");
                    if (scopeClaim is null) return false;
                    var scopes = scopeClaim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return scopes.Contains(adminScope, StringComparer.OrdinalIgnoreCase);
                });
            });

            options.AddPolicy("ScimProvisioning", policy =>
            {
                policy.AuthenticationSchemes.Add("ScimBearer");
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("client_id");
            });
        });

        services.AddCors();
        // The provider subscribes to the cluster bus so a client write drops the cached origins on
        // every node; AddAuthagonal supplies the in-process default, this host has to as well.
        services.TryAddSingleton<Authagonal.Core.Clustering.IClusterEventBus,
            Authagonal.Core.Clustering.InProcessClusterEventBus>();
        services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();
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
