using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Authagonal.Bff;

/// <summary>
/// Mints and caches RFC 8693 context-bound tokens for a BFF session: the session's access token is
/// exchanged (with host extension params such as <c>project_id</c>) for a downscoped token, cached
/// per (session, params) in the shared distributed cache for the exchanged token's remaining
/// lifetime. Used by the ws-ticket endpoint and the token-injecting proxy — the browser never sees
/// either token. A denied exchange (the tenant's transformer rejected the binding) returns null.
/// </summary>
internal sealed class BffExchangedTokens(
    ITokenClient tokenClient,
    IBffTenantResolver tenants,
    IDistributedCache cache,
    ILogger<BffExchangedTokens> logger)
{
    // Skew between the exchanged token's expiry and the cache entry so a just-served cached token
    // is never already dead on arrival at the upstream.
    private static readonly TimeSpan CacheSkew = TimeSpan.FromSeconds(30);

    public async Task<string?> GetOrExchangeAsync(
        BffSession session,
        string accessToken,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct)
    {
        var key = CacheKey(session.SessionId, extraParameters);
        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null)
            return cached;

        var tenant = await tenants.ResolveAsync(session.TenantKey, ct);
        if (tenant is null)
            return null;

        try
        {
            var result = await tokenClient.ExchangeTokenAsync(tenant, accessToken, extraParameters, scope: null, ct);
            var ttl = TimeSpan.FromSeconds(result.ExpiresIn) - CacheSkew;
            if (ttl > TimeSpan.FromSeconds(5))
                await cache.SetStringAsync(key, result.AccessToken,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
            return result.AccessToken;
        }
        catch (BffTokenException ex)
        {
            // invalid_target = the transformer refused the binding (no access to that context);
            // treated as a denial, not an error — the caller surfaces 403.
            logger.LogInformation("BFF context-token exchange denied/failed for session {SessionId}: {Message}",
                session.SessionId, ex.Message);
            return null;
        }
    }

    private static string CacheKey(string sessionId, IReadOnlyDictionary<string, string> extraParameters)
    {
        var parts = extraParameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return $"agbff:xt:{sessionId}:{string.Join('&', parts)}";
    }
}
