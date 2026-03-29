using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Authagonal.Server.Services.Saml;
using Azure.Data.Tables;
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

    public InMemoryUserStore UserStore { get; } = new();
    public InMemoryClientStore ClientStore { get; } = new();
    public InMemoryGrantStore GrantStore { get; } = new();
    public InMemorySigningKeyStore SigningKeyStore { get; } = new();
    public InMemorySsoDomainStore SsoDomainStore { get; } = new();
    public InMemorySamlProviderStore SamlProviderStore { get; } = new();
    public InMemoryOidcProviderStore OidcProviderStore { get; } = new();
    public InMemoryUserProvisionStore UserProvisionStore { get; } = new();
    public TestEmailService EmailService { get; } = new();
    public TestAuthHook AuthHook { get; } = new();

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
        });

        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = AdminClientId,
            ClientName = "Admin Client",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [passwordHasher.HashPassword(AdminClientSecret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", "authagonal-admin"],
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

    /// <summary>Get an admin bearer token via client_credentials grant.</summary>
    public async Task<string> GetAdminTokenAsync(HttpClient? client = null)
    {
        client ??= CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "authagonal-admin"
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

        // Extensibility test doubles
        services.AddSingleton<IEmailService>(EmailService);
        services.AddSingleton<IAuthHook>(AuthHook);
        services.AddSingleton<IProvisioningOrchestrator>(new TestProvisioningOrchestrator());
        services.AddSingleton<ISecretProvider>(new PlaintextSecretProvider());

        // Core services (mirrors AddAuthagonal minus storage)
        services.AddLocalization();
        services.AddDataProtection();
        services.AddSingleton(new PasswordPolicy());
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordValidator>();
        services.AddSingleton<KeyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<KeyManager>());
        services.AddScoped<AuthorizationCodeService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddHttpClient("Provisioning");
        services.AddHttpClient("SamlMetadata");
        services.AddHttpClient("OidcDiscovery");
        services.AddMemoryCache();

        // SAML/OIDC services (needed for endpoint routing to resolve parameters)
        var dummyTableClient = new TableClient("UseDevelopmentStorage=true", "dummy");
        services.AddKeyedSingleton("SamlReplayCache", dummyTableClient);
        services.AddKeyedSingleton("OidcStateStore", dummyTableClient);
        services.AddSingleton<SamlMetadataParser>();
        services.AddSingleton<SamlResponseParser>();
        services.AddSingleton<SamlReplayCache>(sp =>
            new SamlReplayCache(sp.GetRequiredKeyedService<TableClient>("SamlReplayCache")));
        services.AddSingleton<OidcDiscoveryClient>();
        services.AddSingleton<OidcStateStore>(sp =>
            new OidcStateStore(sp.GetRequiredKeyedService<TableClient>("OidcStateStore")));

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
                if (user is null || !string.Equals(user.SecurityStamp ?? "", stampClaim ?? "", StringComparison.Ordinal))
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
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidateIssuerSigningKey = true
            };
        });

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp.GetRequiredService<KeyManager>()));

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
        });

        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();
        services.AddHealthChecks().AddCheck<TableStorageHealthCheck>("table_storage");

        _app = builder.Build();

        // Pipeline (mirrors UseAuthagonal + MapAuthagonalEndpoints)
        _app.UseAuthagonal();
        _app.MapAuthagonalEndpoints();

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
