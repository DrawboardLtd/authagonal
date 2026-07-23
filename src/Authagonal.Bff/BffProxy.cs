using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Authagonal.Bff;

/// <summary>The BFF token-injecting proxy: <c>{BasePath}/api/**</c>. Requires the anti-forgery header + a
/// valid session, refreshes the access token if needed, forwards the request to the matched upstream with
/// <c>Authorization: Bearer</c> attached (and the session cookie stripped), and streams the response back.</summary>
internal static class BffProxy
{
    // Hop-by-hop headers (never forwarded) + the ones we deliberately strip: the session cookie must never
    // reach the upstream, and we set our own Authorization / Host.
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Cookie", "Authorization",
    };

    // Match a prefix only on a segment boundary, so "/id" doesn't capture "/identity/..." (which
    // StripPrefix would then mangle into a slash-less "entity/..."). Internal for unit testing.
    internal static bool PrefixMatches(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal)
        && (path.Length == prefix.Length || prefix.EndsWith('/') || path[prefix.Length] == '/');

    // First exchange-route pattern matching the proxied path wins. A pattern is segments matched
    // as a PREFIX of the path's segments; "{param}" placeholders capture their segment (the LAST
    // placeholder names the exchange parameter — earlier ones are positional wildcards like
    // "{apiver}"). A "{param:guid}" placeholder only matches a GUID segment, which is what keeps a
    // broad pattern like "/{apiver}/{project_id:guid}" from capturing literal-segment routes
    // ("/v1/user/profile") and wrongly demanding an exchange for them. Internal for unit testing.
    internal static bool TryMatchExchangeRoute(
        IEnumerable<BffExchangeRoute> routes, string apiPath, out string paramName, out string paramValue)
    {
        var pathSegments = apiPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.PathPattern)) continue;
            var patternSegments = route.PathPattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (patternSegments.Length == 0 || pathSegments.Length < patternSegments.Length) continue;

            string? name = null, value = null;
            var matched = true;
            for (var i = 0; i < patternSegments.Length && matched; i++)
            {
                var p = patternSegments[i];
                if (p.Length > 2 && p[0] == '{' && p[^1] == '}')
                {
                    var placeholder = p[1..^1];
                    var constraintIdx = placeholder.IndexOf(':');
                    var constraint = constraintIdx >= 0 ? placeholder[(constraintIdx + 1)..] : null;
                    if (constraint is not null)
                        placeholder = placeholder[..constraintIdx];

                    matched = constraint switch
                    {
                        null => true,
                        "guid" => Guid.TryParse(pathSegments[i], out _),
                        _ => false, // unknown constraint: never match rather than silently over-match
                    };
                    if (matched)
                    {
                        name = placeholder;
                        value = pathSegments[i];
                    }
                }
                else
                {
                    matched = string.Equals(p, pathSegments[i], StringComparison.OrdinalIgnoreCase);
                }
            }

            if (matched && name is not null && !string.IsNullOrEmpty(value))
            {
                paramName = name;
                paramValue = value;
                return true;
            }
        }

        paramName = string.Empty;
        paramValue = string.Empty;
        return false;
    }

    public static async Task<IResult> ProxyAsync(
        HttpContext ctx,
        IOptions<AuthagonalBffOptions> options,
        IBffSessionStore store,
        BffRefreshCoordinator refresher,
        IHttpClientFactory httpClientFactory,
        BffExchangedTokens exchangedTokens,
        CancellationToken ct)
    {
        var o = options.Value;
        if (!ctx.Request.Headers.ContainsKey(o.AntiForgeryHeader))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        // Resolve the session when present. With AllowAnonymousProxyRequests, a missing or dead session
        // downgrades the call to anonymous (no Authorization attached) instead of a hard 401 — the
        // upstream's own auth decides, matching how the SPA called the API before the BFF.
        BffSession? fresh = null;
        if (ctx.Request.Cookies.TryGetValue(o.CookieName, out var sessionId) && !string.IsNullOrEmpty(sessionId))
        {
            var session = await store.GetAsync(sessionId, ct);
            if (session is not null && session.ExpiresAt > DateTimeOffset.UtcNow)
                fresh = await refresher.EnsureFreshAsync(session, ct);
        }
        if (fresh is null && !o.AllowAnonymousProxyRequests)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        // Path after {BasePath}/api, e.g. "/orders/123".
        var apiBase = o.BasePath + "/api";
        var fullPath = ctx.Request.Path.Value ?? string.Empty;
        var apiPath = fullPath.Length > apiBase.Length ? fullPath[apiBase.Length..] : string.Empty;
        var upstream = o.Upstreams.FirstOrDefault(u => PrefixMatches(apiPath, u.Prefix));
        if (upstream is null)
            return Results.StatusCode(StatusCodes.Status404NotFound);

        // A synthetic routing prefix (StripPrefix) is removed before forwarding so several backends sharing a
        // path namespace can be fanned out from one BFF; a real prefix is left in the forwarded path.
        var forwardedPath = upstream.StripPrefix && PrefixMatches(apiPath, upstream.Prefix)
            ? apiPath[upstream.Prefix.Length..]
            : apiPath;
        var targetUrl = upstream.TargetBaseUrl.TrimEnd('/') + forwardedPath + ctx.Request.QueryString;
        var client = httpClientFactory.CreateClient("AuthagonalBffProxy");

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), targetUrl);
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstreamReq.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } contentType)
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        foreach (var h in ctx.Request.Headers)
        {
            if (NotForwarded.Contains(h.Key) || h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            upstreamReq.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }
        if (fresh is not null)
        {
            // Context-bound routes ride a downscoped exchanged token (cached per session+binding)
            // instead of the primary access token; a denied exchange is a 403, mirroring what the
            // upstream would decide but without spending the request.
            var bearer = fresh.AccessToken;
            if (o.ExchangeRoutes.Count > 0
                && TryMatchExchangeRoute(o.ExchangeRoutes, apiPath, out var paramName, out var paramValue))
            {
                var exchanged = await exchangedTokens.GetOrExchangeAsync(
                    fresh, fresh.AccessToken,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [paramName] = paramValue }, ct);
                if (exchanged is null)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                bearer = exchanged;
            }

            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var upstreamResp = await client.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        foreach (var h in upstreamResp.Headers)
            if (!NotForwarded.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in upstreamResp.Content.Headers)
            if (!h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers[h.Key] = h.Value.ToArray();

        await upstreamResp.Content.CopyToAsync(ctx.Response.Body, ct);
        return Results.Empty;
    }
}
