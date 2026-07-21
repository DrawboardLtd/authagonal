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

    public static async Task<IResult> ProxyAsync(
        HttpContext ctx,
        IOptions<AuthagonalBffOptions> options,
        IBffSessionStore store,
        BffRefreshCoordinator refresher,
        IHttpClientFactory httpClientFactory,
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
        var upstream = o.Upstreams.FirstOrDefault(u => apiPath.StartsWith(u.Prefix, StringComparison.Ordinal));
        if (upstream is null)
            return Results.StatusCode(StatusCodes.Status404NotFound);

        // A synthetic routing prefix (StripPrefix) is removed before forwarding so several backends sharing a
        // path namespace can be fanned out from one BFF; a real prefix is left in the forwarded path.
        var forwardedPath = upstream.StripPrefix && apiPath.StartsWith(upstream.Prefix, StringComparison.Ordinal)
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
            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh.AccessToken);

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
