using System.Net.Http.Headers;
using Authagonal.Core.Authority;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

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

        // Forwarding metadata. These were copied through verbatim, and the proxy never asserted its
        // own — so from the upstream's point of view the BFF, a trusted reverse proxy, had just
        // vouched for whatever client IP, host and scheme the caller chose to send. None of them is
        // on the fetch spec's forbidden-header list, so any script in the SPA's origin could set
        // them. They are re-set from server-side state below.
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port",
        "X-Forwarded-Prefix", "X-Real-IP", "Forwarded",
    };

    /// <summary>
    /// Builds the upstream URL and confirms it still addresses the configured upstream. False means
    /// the composition escaped — the request must not be sent.
    /// </summary>
    /// <remarks>
    /// The relative part is forced to start with '/' so it can never be read as a host label, and the
    /// resulting authority and scheme are compared against the base. Both halves matter: the
    /// normalisation prevents the escape, the comparison proves it.
    /// </remarks>
    internal static bool TryComposeTarget(string targetBaseUrl, string forwardedPath, string? query, out string targetUrl)
    {
        targetUrl = string.Empty;

        if (!Uri.TryCreate(targetBaseUrl, UriKind.Absolute, out var baseUri)) return false;

        var relative = forwardedPath.Length == 0 || forwardedPath[0] == '/'
            ? forwardedPath
            : "/" + forwardedPath;

        if (!Uri.TryCreate(baseUri, relative, out var composed)) return false;

        if (!string.Equals(composed.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(composed.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        targetUrl = baseUri.GetLeftPart(UriPartial.Authority)
            + baseUri.AbsolutePath.TrimEnd('/') + relative + query;
        return true;
    }

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
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BffProxy));
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

        // Composed as a URI and re-checked, not concatenated. PrefixMatches accepts any prefix ending
        // in '/', and for those the slice above removes the LEADING slash — so forwardedPath became a
        // relative string and string concatenation fused it onto the host label rather than the path.
        // BffUpstream.Prefix defaults to "/", so a host that merely sets StripPrefix on a
        // default-prefix upstream was in that state, and the first path segment after {BasePath}/api
        // is fully caller-controlled: it could name any host, and the request went there carrying the
        // session's bearer token.
        if (!TryComposeTarget(upstream.TargetBaseUrl, forwardedPath, ctx.Request.QueryString.Value, out var targetUrl))
        {
            logger.LogError(
                "Refusing to proxy: composed target left the configured upstream authority. Upstream {Base}, path {Path}",
                upstream.TargetBaseUrl, forwardedPath);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        var client = httpClientFactory.CreateClient("AuthagonalBffProxy");

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), targetUrl);

        // Decided by the METHOD, not by the framing headers.
        //
        // The old condition was `ContentLength > 0 || has Transfer-Encoding`. Over HTTP/2 — which
        // every browser uses for HTTPS and Kestrel enables by default — RFC 9113 §8.2.2 forbids
        // Transfer-Encoding and Content-Length is optional, so a request whose length is not known in
        // advance satisfied neither condition. The entire payload was discarded, along with
        // Content-Type (re-added inside the same branch), and the request was still forwarded — with
        // the user's bearer token attached — as a bodyless call. An upstream that treats a POST with
        // no body as "clear this" rather than "reject this" then acts on an authenticated request the
        // user never made.
        //
        // ContentLength == 0 is an explicit "no body" and stays one; a null length with a
        // body-bearing method is the streamed case that has to be forwarded.
        var method = ctx.Request.Method;
        var bodyless = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsDelete(method) || HttpMethods.IsTrace(method)
            || HttpMethods.IsOptions(method);

        if (!bodyless && ctx.Request.ContentLength != 0)
        {
            upstreamReq.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } contentType)
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            // Forwarded with the body it describes: an upstream cannot decode a compressed payload it
            // was not told was compressed.
            if (ctx.Request.Headers.ContentEncoding.Count > 0)
                upstreamReq.Content.Headers.TryAddWithoutValidation(
                    "Content-Encoding", ctx.Request.Headers.ContentEncoding.ToArray());
        }
        foreach (var h in ctx.Request.Headers)
        {
            if (NotForwarded.Contains(h.Key) || h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            upstreamReq.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }

        // Asserted by the proxy from what it actually observed, replacing anything the caller sent.
        if (ctx.Connection.RemoteIpAddress is { } peer)
            upstreamReq.Headers.TryAddWithoutValidation("X-Forwarded-For", peer.ToString());
        upstreamReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", ctx.Request.Scheme);
        if (ctx.Request.Host.HasValue)
            upstreamReq.Headers.TryAddWithoutValidation("X-Forwarded-Host", ctx.Request.Host.Value);
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

            // Authority gate: the outgoing bearer (post-exchange, so the token that will
            // actually be presented) must permit every declared type:action pair AT THIS UPSTREAM.
            //
            // The location is the request's own destination, which the proxy has already composed and
            // proved stays inside the configured upstream. Without it a grant's `locations` narrowed
            // nothing anywhere in the product: it was parsed, intersected and written into the token,
            // and then every evaluator ignored it, so a token scoped to one resource server spent its
            // authority at any other one the same BFF fronted.
            if (upstream.RequiredAuthority.Count > 0
                && !PermitsRequiredAuthority(
                    bearer, upstream.RequiredAuthority,
                    AuthorityLocationFor(upstream, forwardedPath), upstream.StrictAuthority))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (upstream.RequiredAuthority.Count > 0)
        {
            // An authority-gated route is never anonymous, whatever AllowAnonymousProxyRequests says.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
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

    /// <summary>The RFC 9396 location this request is spending authority at: the upstream's declared
    /// location root (or its target base URL) with the proxied path appended, so a grant may pin
    /// authority to a sub-tree and not merely to a host. Internal for unit testing.</summary>
    internal static string AuthorityLocationFor(BffUpstream upstream, string forwardedPath)
    {
        var root = (string.IsNullOrWhiteSpace(upstream.AuthorityLocation)
            ? upstream.TargetBaseUrl
            : upstream.AuthorityLocation).TrimEnd('/');

        if (forwardedPath.Length == 0) return root;
        return forwardedPath[0] == '/' ? root + forwardedPath : root + "/" + forwardedPath;
    }

    /// <summary>Evaluates the bearer's RFC 9396 authorization_details claim against the
    /// route's "type:action" requirements. No claim = unrestricted (legacy scope-based
    /// tokens); a garbled claim or a malformed requirement fails closed. Internal for unit
    /// testing.</summary>
    /// <param name="location">Where the authority is being spent — a grant naming locations is
    /// honoured only at those. Null skips the check (the pre-location behaviour).</param>
    /// <param name="strict">Refuse a grant carrying a constraint the proxy supplies no context for,
    /// rather than forwarding and leaving it to the upstream.</param>
    internal static bool PermitsRequiredAuthority(
        string bearer, IEnumerable<string> requiredPairs, string? location = null, bool strict = false)
    {
        AuthoritySet authority;
        try
        {
            var jwt = new JsonWebToken(bearer);
            authority = jwt.TryGetPayloadValue<System.Text.Json.JsonElement>("authorization_details", out var element)
                ? AuthorityEvaluator.FromClaimValue(element.GetRawText())
                : AuthoritySet.Unrestricted;
        }
        catch (ArgumentException)
        {
            return false; // not a JWT — nothing to evaluate, fail closed on a gated route
        }

        foreach (var pair in requiredPairs)
        {
            // Last colon splits type from action: connector types may themselves be
            // namespaced ("mcp:tools.internal:search_docs" → type "mcp:tools.internal").
            var separator = pair.LastIndexOf(':');
            if (separator <= 0 || separator == pair.Length - 1)
                return false;
            if (!authority.Permits(pair[..separator], pair[(separator + 1)..], location, context: null, strict))
                return false;
        }
        return true;
    }
}
