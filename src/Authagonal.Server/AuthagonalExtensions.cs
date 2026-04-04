using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Endpoints.Admin;
using Authagonal.Server.Endpoints.Scim;
using Authagonal.Server.Middleware;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;
using Authagonal.Server.Services.Oidc;
using Authagonal.Server.Services.Saml;
using Authagonal.Storage;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Fido2NetLib;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace Authagonal.Server;

/// <summary>
/// Extension methods for composing Authagonal into any ASP.NET Core application.
/// This is the primary integration point for hosting Authagonal as a library.
/// </summary>
public static class AuthagonalExtensions
{
    /// <summary>
    /// Registers all Authagonal services: storage, authentication, authorization, CORS,
    /// rate limiting, health checks, and background tasks.
    /// <para>
    /// Override extensibility points by registering your implementations <b>before</b> calling this method:
    /// <c>IEmailService</c>, <c>IAuthHook</c>, <c>IProvisioningOrchestrator</c>, <c>ISecretProvider</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAuthagonal(this IServiceCollection services, IConfiguration configuration)
    {
        // ---------------------------------------------------------------------------
        // Localization
        // ---------------------------------------------------------------------------
        services.AddLocalization();

        // ---------------------------------------------------------------------------
        // Tenant context — default single-tenant reads from IConfiguration.
        // Multi-tenant hosts (e.g. Cloud) register their own scoped ITenantContext
        // before calling AddAuthagonal; TryAdd ensures it is not overwritten.
        // ---------------------------------------------------------------------------
        services.TryAddSingleton<Authagonal.Core.Services.ITenantContext>(
            sp => new DefaultTenantContext(sp.GetRequiredService<IConfiguration>()));

        // ---------------------------------------------------------------------------
        // Storage — skip if stores are already registered (e.g. by a multi-tenant host)
        // ---------------------------------------------------------------------------
        var storageConnectionString = configuration["Storage:ConnectionString"];

        if (!services.Any(d => d.ServiceType == typeof(Authagonal.Core.Stores.IUserStore)))
        {
            if (string.IsNullOrWhiteSpace(storageConnectionString))
                throw new InvalidOperationException("Storage:ConnectionString is not configured");

            services.AddTableStorage(storageConnectionString);
        }

        // ---------------------------------------------------------------------------
        // Data protection — shared key storage for multi-instance deployments.
        // Multi-tenant hosts configure per-tenant data protection separately.
        // ---------------------------------------------------------------------------
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Authagonal");

        var dpBlobUri = configuration["DataProtection:BlobUri"];
        if (!string.IsNullOrWhiteSpace(dpBlobUri))
        {
            // Explicit blob URI: "https://<account>.blob.core.windows.net/<container>/keys.xml"
            dataProtection.PersistKeysToAzureBlobStorage(new Uri(dpBlobUri), new Azure.Identity.DefaultAzureCredential());
        }
        else if (!string.IsNullOrWhiteSpace(storageConnectionString) &&
                 !storageConnectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase))
        {
            // Production storage — auto-create a blob container for data protection keys
            var blobServiceClient = new BlobServiceClient(storageConnectionString);
            var container = blobServiceClient.GetBlobContainerClient("dataprotection");
            container.CreateIfNotExists();
            var blobClient = container.GetBlobClient("keys.xml");
            dataProtection.PersistKeysToAzureBlobStorage(blobClient);
        }
        // else: local dev with Azurite or multi-tenant host — use default file-based keys

        // ---------------------------------------------------------------------------
        // Password policy
        // ---------------------------------------------------------------------------
        var passwordPolicy = new PasswordPolicy();
        configuration.GetSection("PasswordPolicy").Bind(passwordPolicy);
        services.AddSingleton(passwordPolicy);

        // ---------------------------------------------------------------------------
        // Auth / Cache / BackgroundService options
        // ---------------------------------------------------------------------------
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<CacheOptions>(configuration.GetSection("Cache"));
        services.Configure<BackgroundServiceOptions>(configuration.GetSection("BackgroundServices"));

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordValidator>();
        services.AddSingleton<KeyManager>();
        services.TryAddSingleton<Authagonal.Core.Services.IKeyManager>(sp => sp.GetRequiredService<KeyManager>());
        services.AddHostedService(sp => sp.GetRequiredService<KeyManager>());
        services.AddScoped<AuthorizationCodeService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddHostedService<TokenCleanupService>();
        services.AddHostedService<GrantReconciliationService>();
        services.AddHttpClient("Provisioning");
        services.AddHostedService<ClientSeedService>();
        services.AddHostedService<ProviderSeedService>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<RecoveryCodeService>();
        services.AddSingleton<WebAuthnService>();

        // WebAuthn (FIDO2)
        var issuer = configuration["Issuer"] ?? "https://localhost";
        var issuerUri = new Uri(issuer);
        services.AddFido2(options =>
        {
            options.ServerDomain = issuerUri.Host;
            options.ServerName = "Authagonal";
            options.Origins = new HashSet<string> { issuer.TrimEnd('/') };
        });

        // Extensibility points — TryAdd so custom registrations take precedence
        services.TryAddSingleton<IEmailService, NullEmailService>();
        services.TryAddSingleton<IProvisioningOrchestrator, TccProvisioningOrchestrator>();
        // Auth hooks — multiple IAuthHook implementations can be registered and all will run.
        // NullAuthHook is only added if no hooks are registered by the host.
        if (!services.Any(s => s.ServiceType == typeof(IAuthHook)))
            services.AddSingleton<IAuthHook, NullAuthHook>();

        // Secret provider: defaults to plaintext; set SecretProvider:VaultUri to use Key Vault
        var vaultUri = configuration["SecretProvider:VaultUri"];
        if (!string.IsNullOrWhiteSpace(vaultUri))
        {
            var secretClient = new Azure.Security.KeyVault.Secrets.SecretClient(
                new Uri(vaultUri), new Azure.Identity.DefaultAzureCredential());
            services.AddSingleton(secretClient);
            services.AddSingleton<ISecretProvider, KeyVaultSecretProvider>();
        }
        else
        {
            services.TryAddSingleton<ISecretProvider, PlaintextSecretProvider>();
        }

        // ---------------------------------------------------------------------------
        // SAML services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("SamlMetadata");
        services.AddMemoryCache();
        services.AddSingleton<SamlMetadataParser>();
        services.AddSingleton<SamlResponseParser>();
        services.AddSingleton<SamlReplayCache>(sp =>
        {
            var tableClient = sp.GetRequiredKeyedService<TableClient>("SamlReplayCache");
            return new SamlReplayCache(tableClient, sp.GetRequiredService<IOptions<CacheOptions>>());
        });

        // ---------------------------------------------------------------------------
        // OIDC services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("OidcDiscovery");
        services.AddSingleton<OidcDiscoveryClient>();
        services.AddSingleton<OidcStateStore>(sp =>
        {
            var tableClient = sp.GetRequiredKeyedService<TableClient>("OidcStateStore");
            return new OidcStateStore(tableClient, sp.GetRequiredService<IOptions<CacheOptions>>());
        });

        // ---------------------------------------------------------------------------
        // Authentication
        // ---------------------------------------------------------------------------
        var cookieLifetimeHours = configuration.GetValue("Authentication:CookieLifetimeHours", 48);

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(cookieLifetimeHours);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            options.Events.OnValidatePrincipal = async context =>
            {
                var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
                var userId = context.Principal?.FindFirst("sub")?.Value
                    ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (userId is null || stampClaim is null)
                    return;

                var authOpts = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                var lastValidated = context.Properties.GetString("stamp_validated");
                if (lastValidated is not null &&
                    DateTimeOffset.TryParse(lastValidated, out var lastTime) &&
                    DateTimeOffset.UtcNow - lastTime < TimeSpan.FromMinutes(authOpts.SecurityStampRevalidationMinutes))
                {
                    return;
                }

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
            var issuer = configuration["Issuer"]!;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                AudienceValidator = (audiences, _, _) => audiences?.Any() == true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidateIssuerSigningKey = true
            };
        })
        .AddScheme<AuthenticationSchemeOptions, ScimBearerAuthenticationHandler>("ScimBearer", null);

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp.GetRequiredService<Authagonal.Core.Services.IKeyManager>()));

        // ---------------------------------------------------------------------------
        // Authorization
        // ---------------------------------------------------------------------------
        var adminScope = configuration["AdminApi:Scope"] ?? "authagonal-admin";

        services.AddAuthorization(options =>
        {
            options.AddPolicy("IdentityAdmin", policy =>
            {
                policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var scopeClaim = context.User.FindFirst("scope")
                        ?? context.User.FindFirst("scp");

                    if (scopeClaim is null)
                        return false;

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

        // ---------------------------------------------------------------------------
        // CORS
        // ---------------------------------------------------------------------------
        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();

        // ---------------------------------------------------------------------------
        // Cluster — node identity, gossip, leader election, distributed rate limiting
        // ---------------------------------------------------------------------------
        services.Configure<ClusterOptions>(configuration.GetSection("Cluster"));

        var clusterNode = new ClusterNode(Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        services.AddSingleton(clusterNode);
        services.AddSingleton<PeerRegistry>();
        services.AddSingleton<ClusterLeaderService>();

        var rateLimiter = new DistributedRateLimiter(clusterNode);
        services.AddSingleton(rateLimiter);
        services.AddSingleton<IRateLimiter>(rateLimiter);

        var clusterEnabled = configuration.GetValue("Cluster:Enabled", true);
        if (clusterEnabled)
        {
            services.AddHostedService<ClusterDiscoveryService>();
            services.AddHostedService<ClusterGossipService>();
            services.AddHttpClient("ClusterGossip", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(3);
            });
        }

        // ---------------------------------------------------------------------------
        // Health Checks
        // ---------------------------------------------------------------------------
        services.AddHealthChecks()
            .AddCheck<TableStorageHealthCheck>("table_storage");

        return services;
    }

    /// <summary>
    /// Adds Authagonal middleware to the pipeline: exception handling, security headers,
    /// CORS, rate limiting, authentication, authorization, and static file serving.
    /// Call this before <see cref="MapAuthagonalEndpoints"/>.
    /// </summary>
    public static WebApplication UseAuthagonal(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            // Trust all proxies in container environments (Azure Container Apps, k8s, etc.)
            KnownIPNetworks = { new System.Net.IPNetwork(System.Net.IPAddress.Any, 0) },
        });

        app.UseExceptionHandlingMiddleware();

        // SCIM request logging — temporary diagnostic to trace Entra provisioning requests
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/scim"))
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ScimRequestLog");
                var hasAuth = context.Request.Headers.Authorization.Count > 0;
                logger.LogWarning("SCIM request: {Method} {Path}{Query} | Auth={HasAuth} | Host={Host} | UA={UserAgent}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString,
                    hasAuth,
                    context.Request.Host,
                    context.Request.Headers.UserAgent.ToString());

                await next();

                logger.LogWarning("SCIM response: {Method} {Path} => {StatusCode} | ContentType={ContentType}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    context.Response.ContentType);
            }
            else
            {
                await next();
            }
        });

        // Request localization
        var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt" };
        app.UseRequestLocalization(options =>
        {
            options.SetDefaultCulture("en");
            options.AddSupportedCultures(supportedCultures);
            options.AddSupportedUICultures(supportedCultures);
            options.ApplyCurrentCultureToResponseHeaders = true;
        });

        // Security headers
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; object-src 'none'";
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            await next();
        });

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        return app;
    }

    /// <summary>
    /// Maps all Authagonal endpoints: OAuth/OIDC, SAML, authentication, and admin APIs.
    /// Call after <see cref="UseAuthagonal"/>.
    /// </summary>
    public static WebApplication MapAuthagonalEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health").AllowAnonymous();

        app.MapDiscoveryEndpoints();
        app.MapJwksEndpoint();
        app.MapAuthorizeEndpoint();
        app.MapTokenEndpoint();
        app.MapRevocationEndpoint();
        app.MapEndSessionEndpoint();
        app.MapUserinfoEndpoint();

        if (app.Configuration.GetValue("AdminApi:Enabled", true))
        {
            app.MapUserAdminEndpoints();
            app.MapRoleAdminEndpoints();
            app.MapSsoAdminEndpoints();
            app.MapTokenAdminEndpoints();
            app.MapMfaAdminEndpoints();
            app.MapScimTokenAdminEndpoints();
        }

        app.MapScimUserEndpoints();
        app.MapScimGroupEndpoints();
        app.MapScimDiscoveryEndpoints();

        app.MapAuthEndpoints();
        app.MapMfaEndpoints();
        app.MapMfaSetupEndpoints();
        app.MapSamlEndpoints();
        app.MapOidcEndpoints();

        if (app.Configuration.GetValue("Cluster:Enabled", true))
        {
            app.MapClusterEndpoints();
        }

        return app;
    }
}

/// <summary>
/// Wires IKeyManager into JWT bearer token validation at runtime.
/// </summary>
sealed class JwtBearerKeyResolverPostConfigure(Authagonal.Core.Services.IKeyManager keyManager) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) =>
        {
            return keyManager.GetSecurityKeys()
                .Select(jwk =>
                {
                    var rsaKey = new RsaSecurityKey(new System.Security.Cryptography.RSAParameters
                    {
                        Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
                        Exponent = Base64UrlEncoder.DecodeBytes(jwk.E)
                    })
                    { KeyId = jwk.Kid };
                    return (SecurityKey)rsaKey;
                })
                .ToList();
        };
    }
}
