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

    /// <summary>
    /// One gate PER cache key, not one for the whole process.
    /// </summary>
    /// <remarks>
    /// A single process-wide semaphore was held across an unpaged full scan of a tenant's client
    /// table, with no timeout, on the CORS middleware path — which runs for every request. One tenant
    /// with a large client table (or a slow store) therefore stalled CORS resolution for every other
    /// tenant on the node, and a store that hung stalled it indefinitely.
    /// </remarks>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-(tenant, env) cache.
    /// </summary>
    /// <remarks>
    /// It was keyed on tenant alone. Env is a first-class isolation boundary — every store threads it
    /// into the partition key, and a tenant's sandbox envs have their own client records — so the
    /// origins list was built from an env-scoped scan and then cached under a key that did not name
    /// the env. Whichever env warmed the entry first served its origins to all of them, which for a
    /// credentialed policy means a sandbox origin could be honoured against production.
    /// </remarks>
    private readonly ConcurrentDictionary<string, (string[] Origins, DateTimeOffset Expiry)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// How long the client-table scan may take before the cached (or static) answer is used instead.
    /// CORS resolution sits in front of every request; it must not be the thing that hangs.
    /// </summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// True for a syntactically valid Origin: an absolute http(s) URI with no path, query or fragment.
    /// </summary>
    /// <remarks>
    /// Dynamic registration stored <c>allowed_cors_origins</c> with no validation at all, and the
    /// browser compares Origin headers by exact string — so anything that is not an origin is either
    /// inert configuration that looks live, or an attempt to smuggle a wildcard into a policy that
    /// also sets AllowCredentials.
    /// </remarks>
    internal static bool IsValidOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.AbsolutePath != "/") return false;

        // The browser sends the origin without a trailing slash, and matching is exact.
        return string.Equals(origin.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
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

        var tenant = context.RequestServices.GetService<ITenantContext>();
        var cacheKey = $"{tenant?.TenantId ?? "default"}|{tenant?.Env ?? ""}";

        if (_cache.TryGetValue(cacheKey, out var entry) && DateTimeOffset.UtcNow < entry.Expiry)
            return entry.Origins;

        var gate = _gates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Double-check after acquiring the lock.
            if (_cache.TryGetValue(cacheKey, out entry) && DateTimeOffset.UtcNow < entry.Expiry)
                return entry.Origins;

            var staticOrigins = staticOnly;

            var clientOrigins = new List<string>();
            var scanned = false;
            try
            {
                // Resolve IClientStore from the request scope (supports multi-tenant)
                var clientStore = context.RequestServices.GetRequiredService<IClientStore>();
                using var timeout = new CancellationTokenSource(ScanTimeout);
                var clients = await clientStore.GetAllAsync(timeout.Token);
                scanned = true;
                foreach (var client in clients)
                {
                    // A disabled client contributes nothing. Its origins were pooled in regardless, so
                    // disabling a client left its origin able to make credentialed cross-origin calls
                    // to the protocol surface — the one thing an operator disabling it is trying to
                    // stop.
                    if (!client.Enabled) continue;

                    foreach (var origin in client.AllowedCorsOrigins)
                    {
                        // Only a well-formed scheme://host[:port] is honoured. An entry with a path,
                        // a wildcard or trailing junk either never matches (dead configuration that
                        // reads as though it works) or, in the wildcard case, is a request to widen
                        // the policy in a way this provider must not grant.
                        if (IsValidOrigin(origin))
                            clientOrigins.Add(origin);
                        else
                            logger.LogWarning("Ignoring malformed CORS origin {Origin} on client {ClientId}", origin, client.ClientId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load client CORS origins for {CacheKey}", cacheKey);
            }

            if (!scanned)
            {
                // A stale entry beats collapsing to static-origins-only: dropping every client origin
                // because one scan timed out turns a slow store into a CORS outage for every relying
                // party. The entry is not re-dated, so the next request retries.
                if (entry.Origins is { Length: > 0 })
                {
                    logger.LogWarning("Serving stale CORS origins for {CacheKey} after a failed refresh", cacheKey);
                    return entry.Origins;
                }

                return staticOrigins
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var origins = staticOrigins
                .Concat(clientOrigins)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _cache[cacheKey] = (origins, DateTimeOffset.UtcNow.AddMinutes(cacheOptions.Value.CorsCacheMinutes));
            logger.LogDebug("CORS origins cache refreshed for {CacheKey} with {Count} origins", cacheKey, origins.Length);
            return origins;
        }
        finally
        {
            gate.Release();
        }
    }
}
