using System.Collections.Concurrent;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class DynamicCorsPolicyProvider(
    IConfiguration configuration,
    IOptions<CacheOptions> cacheOptions,
    ILogger<DynamicCorsPolicyProvider> logger) : ICorsPolicyProvider
{

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    // Per-tenant cache: single-tenant hosts hold one "default" entry; multi-tenant hosts must not
    // serve one tenant's allowed origins to another, so the cache is keyed by tenant.
    private readonly ConcurrentDictionary<string, (string[] Origins, DateTimeOffset Expiry)> _cache = new();

    /// <summary>
    /// Path prefixes where a CLIENT-registered origin may be honoured: the OAuth/OIDC protocol surface a
    /// browser-based relying party legitimately calls cross-origin.
    /// </summary>
    /// <remarks>
    /// A whitelist, deliberately. This provider ignores <c>policyName</c> and <c>app.UseCors()</c> is called
    /// with none, so whatever it returns applies to EVERY endpoint — previously including the
    /// cookie-authenticated interactive-auth API. Since the policy also sets <c>AllowCredentials</c>, any
    /// origin a client registered could read authenticated responses from <c>/api/auth/*</c>, which covers
    /// the account and consent APIs and <c>POST /api/auth/mfa/recovery/generate</c> — an endpoint that
    /// returns plaintext recovery codes. Dynamic client registration let an anonymous registrant add an
    /// origin to that list, so the whole chain was reachable without credentials.
    /// </remarks>
    private static readonly string[] ClientOriginPathPrefixes =
    [
        "/connect/",        // token, userinfo, revocation, introspection, endsession
        "/.well-known/",    // discovery, jwks
    ];

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var path = context.Request.Path.Value ?? "";
        var clientOriginsAllowed = ClientOriginPathPrefixes.Any(
            p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var origins = await GetAllowedOriginsAsync(context, includeClientOrigins: clientOriginsAllowed);

        if (origins.Length == 0)
            return null;

        var requestOrigin = context.Request.Headers.Origin.ToString();

        if (string.IsNullOrEmpty(requestOrigin) ||
            !origins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var policyBuilder = new CorsPolicyBuilder();

        policyBuilder
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();

        return policyBuilder.Build();
    }

    private async Task<string[]> GetAllowedOriginsAsync(HttpContext context, bool includeClientOrigins)
    {
        var staticOnly = configuration.GetSection("AllowedCorsOrigins").Get<string[]>() ?? [];

        // Outside the protocol surface, only operator-controlled configuration counts. Resolved without
        // touching the client store or the cache, so a registrant cannot influence this at all.
        if (!includeClientOrigins)
        {
            return staticOnly
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var tenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId ?? "default";

        if (_cache.TryGetValue(tenantId, out var entry) && DateTimeOffset.UtcNow < entry.Expiry)
            return entry.Origins;

        await _semaphore.WaitAsync();
        try
        {
            // Double-check after acquiring the lock.
            if (_cache.TryGetValue(tenantId, out entry) && DateTimeOffset.UtcNow < entry.Expiry)
                return entry.Origins;

            var staticOrigins = staticOnly;

            var clientOrigins = new List<string>();
            try
            {
                // Resolve IClientStore from the request scope (supports multi-tenant)
                var clientStore = context.RequestServices.GetRequiredService<IClientStore>();
                var clients = await clientStore.GetAllAsync();
                foreach (var client in clients)
                {
                    clientOrigins.AddRange(client.AllowedCorsOrigins);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load client CORS origins; using static origins only");
            }

            var origins = staticOrigins
                .Concat(clientOrigins)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _cache[tenantId] = (origins, DateTimeOffset.UtcNow.AddMinutes(cacheOptions.Value.CorsCacheMinutes));
            logger.LogDebug("CORS origins cache refreshed for tenant {Tenant} with {Count} origins", tenantId, origins.Length);
            return origins;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
