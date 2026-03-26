using Authagonal.Core.Services;
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
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------
var storageConnectionString = config["Storage:ConnectionString"]
    ?? throw new InvalidOperationException("Storage:ConnectionString is not configured");

builder.Services.AddTableStorage(storageConnectionString);

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<KeyManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KeyManager>());
builder.Services.AddScoped<AuthorizationCodeService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddSingleton<IEmailService, EmailService>();
builder.Services.AddHostedService<TokenCleanupService>();
builder.Services.AddHttpClient("Provisioning");
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();
builder.Services.AddHostedService<ClientSeedService>();

// ---------------------------------------------------------------------------
// SAML services
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient("SamlMetadata");
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SamlMetadataParser>();
builder.Services.AddSingleton<SamlResponseParser>();
builder.Services.AddSingleton<SamlReplayCache>(sp =>
{
    var tableClient = sp.GetRequiredKeyedService<TableClient>("SamlReplayCache");
    return new SamlReplayCache(tableClient);
});

// ---------------------------------------------------------------------------
// OIDC services
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient("OidcDiscovery");
builder.Services.AddSingleton<OidcDiscoveryClient>();
builder.Services.AddSingleton<OidcStateStore>(sp =>
{
    var tableClient = sp.GetRequiredKeyedService<TableClient>("OidcStateStore");
    return new OidcStateStore(tableClient);
});

// ---------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------
var cookieLifetimeHours = config.GetValue("Authentication:CookieLifetimeHours", 48);

builder.Services.AddAuthentication(options =>
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

    // Validate security stamp periodically to detect org changes, password resets, etc.
    options.Events.OnValidatePrincipal = async context =>
    {
        var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
        var userId = context.Principal?.FindFirst("sub")?.Value
            ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (userId is null || stampClaim is null)
            return;

        // Only check every 30 minutes to avoid hitting the DB on every request
        var lastValidated = context.Properties.GetString("stamp_validated");
        if (lastValidated is not null &&
            DateTimeOffset.TryParse(lastValidated, out var lastTime) &&
            DateTimeOffset.UtcNow - lastTime < TimeSpan.FromMinutes(30))
        {
            return;
        }

        var userStore = context.HttpContext.RequestServices.GetRequiredService<Authagonal.Core.Stores.IUserStore>();
        var user = await userStore.FindByIdAsync(userId);

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
    var issuer = config["Issuer"]!;

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = issuer,
        ValidateIssuer = true,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        ValidateIssuerSigningKey = true
    };
});

// Post-configure JWT bearer to wire up the dynamic signing key resolver after DI is available.
// This avoids calling BuildServiceProvider() during configuration.
builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
    new JwtBearerKeyResolverPostConfigure(sp.GetRequiredService<KeyManager>()));

// ---------------------------------------------------------------------------
// Authorization
// ---------------------------------------------------------------------------
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("IdentityAdmin", policy =>
    {
        policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            // Check for scope claim containing "projects-identity-admin"
            var scopeClaim = context.User.FindFirst("scope")
                ?? context.User.FindFirst("scp");

            if (scopeClaim is null)
                return false;

            var scopes = scopeClaim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return scopes.Contains("projects-identity-admin", StringComparer.OrdinalIgnoreCase);
        });
    });
});

// ---------------------------------------------------------------------------
// CORS
// ---------------------------------------------------------------------------
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();

// ---------------------------------------------------------------------------
// Health Checks
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddCheck<TableStorageHealthCheck>("table_storage");

var app = builder.Build();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandlingMiddleware();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------
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

app.Run();

// ---------------------------------------------------------------------------
// Helper: wires KeyManager into JWT bearer token validation at runtime
// ---------------------------------------------------------------------------
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
