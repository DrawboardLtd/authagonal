using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Endpoints.Admin;
using Authagonal.Server.Middleware;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Authagonal.Server.Services.Saml;
using Authagonal.Storage;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Threading.RateLimiting;

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
        // Storage
        // ---------------------------------------------------------------------------
        var storageConnectionString = configuration["Storage:ConnectionString"]
            ?? throw new InvalidOperationException("Storage:ConnectionString is not configured");

        services.AddTableStorage(storageConnectionString);

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<KeyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<KeyManager>());
        services.AddScoped<AuthorizationCodeService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddHostedService<TokenCleanupService>();
        services.AddHostedService<GrantReconciliationService>();
        services.AddHttpClient("Provisioning");
        services.AddHostedService<ClientSeedService>();

        // Extensibility points — TryAdd so custom registrations take precedence
        services.TryAddSingleton<IEmailService, EmailService>();
        services.TryAddSingleton<IProvisioningOrchestrator, TccProvisioningOrchestrator>();
        services.TryAddSingleton<IAuthHook, NullAuthHook>();

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
            return new SamlReplayCache(tableClient);
        });

        // ---------------------------------------------------------------------------
        // OIDC services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("OidcDiscovery");
        services.AddSingleton<OidcDiscoveryClient>();
        services.AddSingleton<OidcStateStore>(sp =>
        {
            var tableClient = sp.GetRequiredKeyedService<TableClient>("OidcStateStore");
            return new OidcStateStore(tableClient);
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

                var lastValidated = context.Properties.GetString("stamp_validated");
                if (lastValidated is not null &&
                    DateTimeOffset.TryParse(lastValidated, out var lastTime) &&
                    DateTimeOffset.UtcNow - lastTime < TimeSpan.FromMinutes(30))
                {
                    return;
                }

                var userStore = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
                var user = await userStore.GetAsync(userId);

                if (user is null || !string.Equals(user.SecurityStamp, stampClaim, StringComparison.Ordinal))
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
        });

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp.GetRequiredService<KeyManager>()));

        // ---------------------------------------------------------------------------
        // Authorization
        // ---------------------------------------------------------------------------
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
                    return scopes.Contains("authagonal-admin", StringComparer.OrdinalIgnoreCase);
                });
            });
        });

        // ---------------------------------------------------------------------------
        // CORS
        // ---------------------------------------------------------------------------
        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();

        // ---------------------------------------------------------------------------
        // Rate Limiting
        // ---------------------------------------------------------------------------
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("auth", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            options.AddPolicy("token", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

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
        app.UseExceptionHandlingMiddleware();

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
        app.UseRateLimiter();
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

        app.MapUserAdminEndpoints();
        app.MapSsoAdminEndpoints();
        app.MapTokenAdminEndpoints();

        app.MapAuthEndpoints();
        app.MapSamlEndpoints();
        app.MapOidcEndpoints();

        return app;
    }
}

/// <summary>
/// Wires KeyManager into JWT bearer token validation at runtime.
/// </summary>
sealed class JwtBearerKeyResolverPostConfigure(KeyManager keyManager) : IPostConfigureOptions<JwtBearerOptions>
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
