using Authagonal.Core.Clustering;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
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
    /// <summary>
    /// Full single-tenant registration. Calls <see cref="AddAuthagonalCore"/> and adds
    /// singleton stores, KeyManager, background services, and other single-tenant infrastructure.
    /// </summary>
    public static IServiceCollection AddAuthagonal(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthagonalCore(configuration);

        // ---------------------------------------------------------------------------
        // Single-tenant storage. Two configuration paths:
        //   - Storage:ConnectionString — connection string with AccountKey (dev / Azurite)
        //   - Storage:TableServiceUri  — managed-identity URI like
        //     https://{account}.table.core.windows.net/. The host is responsible for
        //     granting the workload's identity Storage Table Data Contributor.
        // The MI path is preferred in production; access keys never need to land in
        // a K8s secret.
        // ---------------------------------------------------------------------------
        var storageConnectionString = configuration["Storage:ConnectionString"];
        var tableServiceUri = configuration["Storage:TableServiceUri"];
        // Default true to keep existing config-driven deployments working unchanged.
        // Set false on hosts that don't expose admin name-prefix search to skip
        // the UserFirstNames / UserLastNames index writes (which use a single hot
        // partition and cap throughput at ~2k ops/sec at scale).
        var nameIndexesEnabled = configuration.GetValue("Storage:NameIndexesEnabled", true);
        if (!services.Any(d => d.ServiceType == typeof(Authagonal.Core.Stores.IUserStore)))
        {
            if (!string.IsNullOrWhiteSpace(tableServiceUri))
            {
                services.AddTableStorage(new Uri(tableServiceUri), new Azure.Identity.DefaultAzureCredential(), nameIndexesEnabled);
            }
            else if (!string.IsNullOrWhiteSpace(storageConnectionString))
            {
                services.AddTableStorage(storageConnectionString, nameIndexesEnabled);
            }
            else
            {
                throw new InvalidOperationException("Either Storage:ConnectionString or Storage:TableServiceUri must be configured");
            }
        }

        // Data protection
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Authagonal");
        var dpBlobUri = configuration["DataProtection:BlobUri"];
        if (!string.IsNullOrWhiteSpace(dpBlobUri))
        {
            dataProtection.PersistKeysToAzureBlobStorage(new Uri(dpBlobUri), new Azure.Identity.DefaultAzureCredential());
        }
        else if (!string.IsNullOrWhiteSpace(storageConnectionString) &&
                 !storageConnectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase))
        {
            var blobServiceClient = new BlobServiceClient(storageConnectionString);
            var container = blobServiceClient.GetBlobContainerClient("dataprotection");
            container.CreateIfNotExists();
            var blobClient = container.GetBlobClient("keys.xml");
            dataProtection.PersistKeysToAzureBlobStorage(blobClient);
        }

        // Signing-key management is provided by Authagonal.Protocol's ProtocolKeyManager
        // (registered via AddAuthagonalCore → AddAuthagonalProtocol). Single-tenant hosts
        // get it as an IKeyManager singleton; multi-tenant hosts that registered their own
        // IKeyManager before AddAuthagonalCore keep theirs.

        // JWT key resolver (uses root provider, fine for singleton ProtocolKeyManager)
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp));

        // Background services that depend on keyed TableClient or singleton stores
        services.AddHostedService<TokenCleanupService>();
        services.AddHostedService<GrantReconciliationService>();
        services.AddHostedService<SigningKeyRotationService>();
        services.AddHostedService<ClientSeedService>();
        services.AddHostedService<ProviderSeedService>();

        // SAML replay cache + OIDC state store (keyed TableClient singletons)
        services.AddSingleton<SamlReplayCache>(sp =>
        {
            var tableClient = sp.GetRequiredKeyedService<TableClient>("SamlReplayCache");
            return new SamlReplayCache(tableClient, sp.GetRequiredService<IOptions<CacheOptions>>());
        });
        services.AddSingleton<OidcStateStore>(sp =>
        {
            var tableClient = sp.GetRequiredKeyedService<TableClient>("OidcStateStore");
            return new OidcStateStore(tableClient, sp.GetRequiredService<IOptions<CacheOptions>>());
        });

        // Health check (depends on ISigningKeyStore singleton)
        services.AddHealthChecks()
            .AddCheck<TableStorageHealthCheck>("table_storage");

        return services;
    }

    /// <summary>
    /// Registers core Authagonal services that are safe for both single-tenant and multi-tenant hosts.
    /// Does NOT register stores, KeyManager, background services, or anything that depends on
    /// singleton store resolution. Multi-tenant hosts call this and register their own equivalents.
    /// </summary>
    public static IServiceCollection AddAuthagonalCore(this IServiceCollection services, IConfiguration configuration, Action<ClusteringBuilder>? configureClustering = null)
    {
        // ---------------------------------------------------------------------------
        // Localization
        // ---------------------------------------------------------------------------
        services.AddLocalization();
        services.AddHttpContextAccessor();

        // ---------------------------------------------------------------------------
        // Tenant context — default single-tenant reads from IConfiguration.
        // Multi-tenant hosts (e.g. Cloud) register their own scoped ITenantContext
        // before calling AddAuthagonal; TryAdd ensures it is not overwritten.
        // ---------------------------------------------------------------------------
        services.TryAddSingleton<Authagonal.Core.Services.ITenantContext>(
            sp => new DefaultTenantContext(sp.GetRequiredService<IConfiguration>()));

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
        // Cloudflare Turnstile — opt-in: verification on /login and /register is enforced
        // only when Turnstile:SecretKey is configured; otherwise the flow is unchanged.
        services.Configure<TurnstileOptions>(configuration.GetSection("Turnstile"));
        services.AddHttpClient<TurnstileVerifier>();

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordValidator>();
        services.AddHttpClient("Provisioning");

        // Protocol — token service, key manager (when not pre-registered), auth-code
        // service. Server maps AuthOptions into AuthagonalProtocolOptions so there's one
        // source of truth for key lifetime / rotation / grace window.
        services.AddSingleton<IConfigureOptions<AuthagonalProtocolOptions>>(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            return new ConfigureNamedOptions<AuthagonalProtocolOptions>(Options.DefaultName, o =>
            {
                o.SigningKeyLifetimeDays = auth.SigningKeyLifetimeDays;
                o.SigningKeyCacheRefreshMinutes = auth.SigningKeyCacheRefreshMinutes;
                o.RefreshTokenReuseGraceSeconds = auth.RefreshTokenReuseGraceSeconds;
                o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
        });

        // Server-specific client-secret verifier — uses PasswordHasher so legacy
        // PBKDF2v1, ASP.NET Identity V3, and BCrypt client-secret hashes all verify
        // through the same path as user passwords. Must TryAdd before AddAuthagonalProtocol.
        services.TryAddSingleton<IClientSecretVerifier, PasswordHasherClientSecretVerifier>();

        services.AddAuthagonalProtocol(_ => { });

        // Subject resolver — maps ClaimsPrincipal / OidcSubject back to AuthUser via the user store.
        services.AddScoped<UserStoreOidcSubjectResolver>();
        services.AddScoped<IOidcSubjectResolver>(sp => sp.GetRequiredService<UserStoreOidcSubjectResolver>());
        services.AddSingleton<TotpService>();
        services.AddSingleton<RecoveryCodeService>();
        services.AddScoped<WebAuthnService>();

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
        services.TryAddSingleton<IAuditLogger, NullAuditLogger>();
        services.TryAddSingleton<IClientScopeGuard, AllowAllClientScopeGuard>();
        services.TryAddSingleton<IProvisioningAppQuota, UnlimitedProvisioningAppQuota>();
        // SCIM group → role mappings (empty default; the cloud registers a per-tenant store).
        services.TryAddSingleton<IScimGroupRoleMappingStore, InMemoryScimGroupRoleMappingStore>();
        services.TryAddScoped<IProvisioningAppProvider, ConfigProvisioningAppProvider>();
        services.TryAddScoped<IProvisioningOrchestrator, TccProvisioningOrchestrator>();
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
        // ---------------------------------------------------------------------------
        // OIDC services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("OidcDiscovery");
        services.AddSingleton<OidcDiscoveryClient>();

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
            // Behind a TLS-terminating proxy that forwards X-Forwarded-Proto, SameAsRequest already
            // yields a Secure cookie. Operators can force Secure unconditionally via config.
            options.Cookie.SecurePolicy = configuration.GetValue("Authentication:AlwaysSecureCookie", false)
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;

            options.Events.OnValidatePrincipal = async context =>
            {
                var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
                var userId = context.Principal?.FindFirst("sub")?.Value
                    ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (userId is null || stampClaim is null)
                    return;

                // Absolute session expiration — reject sessions older than 7 days
                // regardless of sliding renewal to prevent indefinite session extension
                if (context.Properties.IssuedUtc is DateTimeOffset issuedUtc &&
                    DateTimeOffset.UtcNow - issuedUtc > TimeSpan.FromDays(7))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

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

            // Expected audience(s) for resources protected by this scheme. When configured (Audience
            // and/or an Audiences array), tokens MUST carry a matching aud — so a token minted for a
            // different client in the same tenant can't be replayed against this resource. When unset,
            // fall back to "any audience present" (backward-compatible — but resource servers should
            // set Audience to close cross-client audience confusion).
            var expectedAudiences = new List<string>();
            if (configuration["Audience"] is { Length: > 0 } aud) expectedAudiences.Add(aud);
            expectedAudiences.AddRange(configuration.GetSection("Audiences").Get<string[]>() ?? []);

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidAlgorithms = ["ES256"], // pin the signing alg (defence-in-depth vs alg confusion)
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidateIssuerSigningKey = true
            };

            if (expectedAudiences.Count > 0)
                options.TokenValidationParameters.ValidAudiences = expectedAudiences;
            else
                options.TokenValidationParameters.AudienceValidator = (audiences, _, _) => audiences?.Any() == true;

            // Enforce access-token revocation: a token whose jti is in the revoked store is rejected
            // even though it is still cryptographically valid and unexpired.
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
        .AddScheme<AuthenticationSchemeOptions, ScimBearerAuthenticationHandler>("ScimBearer", null);

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
        // Cluster — leader election + cross-node event bus (pluggable backend).
        // Defaults to single-node in-process; the cloud swaps in the Azure-storage backend
        // via configureClustering (UseAzureStorage / UseAzureStorageBus).
        // ---------------------------------------------------------------------------
        services.AddAuthagonalClustering(configuration, configureClustering);

        // Rate limiting is in-process per node; the authoritative global limit is enforced at the edge.
        services.TryAddSingleton<IRateLimiter, InProcessRateLimiter>();

        return services;
    }

    /// <summary>
    /// Adds Authagonal middleware to the pipeline: exception handling, security headers,
    /// CORS, rate limiting, authentication, authorization, and static file serving.
    /// Call this before <see cref="MapAuthagonalEndpoints"/>.
    /// </summary>
    public static WebApplication UseAuthagonal(this WebApplication app)
    {
        // Forwarded-header trust is config-driven so X-Forwarded-For can't be spoofed to forge the
        // client IP that rate-limiting / lockout keys on. Defaults: ForwardLimit=1 (honour only the
        // single hop the ingress appends; ignore anything further left in the chain). For the
        // strongest guarantee set ForwardedHeaders:KnownNetworks to the ingress/pod CIDR (and/or
        // ForwardedHeaders:KnownProxies) so only that proxy may set the client IP.
        var fhOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            ForwardLimit = app.Configuration.GetValue("ForwardedHeaders:ForwardLimit", 1),
        };
        // Start from an empty trust set (the framework default of loopback-only would ignore XFF
        // entirely behind a non-loopback ingress).
        fhOptions.KnownProxies.Clear();
#if NET10_0_OR_GREATER
        fhOptions.KnownIPNetworks.Clear();
#else
        fhOptions.KnownNetworks.Clear();
#endif
        foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
                fhOptions.KnownProxies.Add(ip);
        }
        foreach (var network in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            var parts = network.Split('/');
            if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
#if NET10_0_OR_GREATER
                fhOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
#else
                fhOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
#endif
        }
        app.UseForwardedHeaders(fhOptions);

        app.UseExceptionHandlingMiddleware();

        // Request localization
        var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt" };
        app.UseRequestLocalization(options =>
        {
            options.SetDefaultCulture("en");
            options.AddSupportedCultures(supportedCultures);
            options.AddSupportedUICultures(supportedCultures);
            options.ApplyCurrentCultureToResponseHeaders = true;
        });

        // Security headers. When Cloudflare Turnstile is configured, the CSP must allow its
        // script and challenge iframe (challenges.cloudflare.com); otherwise the widget is
        // blocked and Turnstile-gated forms (login/register/forgot/reset) can never produce a
        // token, leaving their submit buttons permanently disabled. Tight default otherwise.
        var turnstileConfigured = !string.IsNullOrWhiteSpace(
            app.Services.GetService<IOptions<TurnstileOptions>>()?.Value.SiteKey);
        var csp = turnstileConfigured
            ? "default-src 'self'; script-src 'self' https://challenges.cloudflare.com; " +
              "frame-src https://challenges.cloudflare.com; img-src 'self' data: https:; " +
              "style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; object-src 'none'"
            : "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; object-src 'none'";

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] = csp;
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
        app.MapConsentEndpoints();
        app.MapTokenEndpoint();
        app.MapRevocationEndpoint();
        app.MapIntrospectionEndpoint();
        app.MapBackChannelLogoutEndpoints();
        app.MapDeviceAuthorizationEndpoints();
        app.MapEndSessionEndpoint();
        app.MapUserinfoEndpoint();
        app.MapClientRegistrationEndpoint();
        app.MapProtocolPushedAuthorizationEndpoint();

        if (app.Configuration.GetValue("AdminApi:Enabled", true))
        {
            app.MapUserAdminEndpoints();
            app.MapRoleAdminEndpoints();
            app.MapScopeAdminEndpoints();
            app.MapClientAdminEndpoints();
            app.MapProvisioningAdminEndpoints();
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

        return app;
    }
}

/// <summary>
/// Wires IKeyManager into JWT bearer token validation at runtime.
/// Resolves IKeyManager per-request via IHttpContextAccessor to support scoped (multi-tenant) lifetimes.
/// </summary>
public sealed class JwtBearerKeyResolverPostConfigure(IServiceProvider rootProvider) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, securityToken, _, _) =>
        {
            var httpContextAccessor = rootProvider.GetRequiredService<IHttpContextAccessor>();
            var sp = httpContextAccessor.HttpContext?.RequestServices ?? rootProvider;
            var keyManager = sp.GetRequiredService<Authagonal.Core.Services.IKeyManager>();
            return keyManager.GetSecurityKeys()
                .Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey)
                .ToList();
        };
    }
}
