using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Authorizes requests to internal-only endpoints (the <c>/_internal/*</c> routes: cluster coordination
/// and back-channel logout). These must never be invokable by external callers —
/// <c>/_internal/backchannel-logout</c> revokes every grant for an arbitrary subject.
/// <para>
/// A shared secret (<c>Cluster:Secret</c>) presented in the <c>X-Cluster-Secret</c> header, compared in
/// constant time, is the only real credential. With no secret configured the routes are reachable only
/// from the loopback interface, for single-node development.
/// </para>
/// </summary>
/// <remarks>
/// This used to fall back to "the source address looks private" whenever no secret was set, reading
/// <c>Connection.RemoteIpAddress</c> — which <c>UseForwardedHeaders</c> has already OVERWRITTEN from the
/// client-supplied <c>X-Forwarded-For</c> header by the time an endpoint runs. Combined with a trust set
/// that defaulted to empty (meaning every client was a trusted proxy), any internet caller could present
/// <c>X-Forwarded-For: 10.0.0.1</c> and pass. The result was remote unauthenticated mass session
/// destruction for arbitrary subjects. A source address is not a credential; the two remaining checks
/// here both use the RAW peer address captured before forwarded headers are applied.
/// </remarks>
public static class InternalEndpointGuard
{
    public const string SecretHeader = "X-Cluster-Secret";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key holding the untampered peer address, stashed by the middleware
    /// that runs ahead of <c>UseForwardedHeaders</c>. Absent means the middleware was not registered, which
    /// is treated as untrusted.
    /// </summary>
    public const string RawPeerAddressItem = "Authagonal.RawPeerAddress";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key recording whether the operator DECLARED the proxy in front of
    /// this process (<c>ForwardedHeaders:KnownProxies</c> / <c>:KnownNetworks</c>). Absent means no.
    /// </summary>
    public const string ProxyTrustDeclaredItem = "Authagonal.ProxyTrustDeclared";

    /// <summary>
    /// The client address a per-source quota may be keyed on: the forwarded client IP when the operator
    /// declared the proxy that supplies it, and the raw peer address otherwise.
    /// </summary>
    /// <remarks>
    /// <c>Connection.RemoteIpAddress</c> is not that address. With no proxy declared, <c>UseAuthagonal</c>
    /// still honours <c>X-Forwarded-For</c> from the loopback/private ranges — a deliberate guess that
    /// beats the framework's honour-it-from-anybody default for logging — so any caller whose immediate
    /// peer sits in those ranges and does not append its own XFF (an L4 load balancer, a docker bridge,
    /// pod-to-pod) writes the value the rewrite lands on. A limiter keyed on it therefore hands the
    /// attacker a fresh bucket per request, which is the same as having no limiter: registration flooding
    /// and DCR client-record flooding become unbounded from one host.
    /// <para>
    /// The declared case is different in kind, not in degree: there the header can only have been set by
    /// the named proxy, so it is evidence rather than a guess. That is the same line
    /// <c>UseAuthagonal</c> already draws for <c>X-Forwarded-Proto</c>, and the reason the fallback ranges
    /// are documented as never load-bearing for a security decision — a quota is one.
    /// </para>
    /// </remarks>
    public static string TrustedClientAddress(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ProxyTrustDeclaredItem, out var declared) && declared is true)
            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Undeclared: fall back to the peer we actually observed. Behind an L7 proxy every client
        // collapses into one bucket, which throttles harder than intended rather than not at all — and
        // the operator fixes it by declaring the proxy, which is what makes the header trustworthy.
        return RawPeerAddress(httpContext)?.ToString() ?? "unknown";
    }

    public static bool IsAuthorized(HttpContext httpContext, string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            var provided = httpContext.Request.Headers[SecretHeader].ToString();
            return !string.IsNullOrEmpty(provided) &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(secret));
        }

        // No secret: loopback only, and only against the pre-forwarding peer address. Private-range
        // addresses are NOT accepted — in a shared cluster network that would trust every neighbouring
        // workload, and it is exactly what the forged-header bypass impersonated.
        var raw = RawPeerAddress(httpContext);
        return raw is not null && IPAddress.IsLoopback(raw);
    }

    /// <summary>
    /// The peer address as observed by Kestrel, before <c>UseForwardedHeaders</c> could replace it.
    /// Falls back to the live connection address only when nothing was stashed AND no forwarded header is
    /// present, so a spoofed header can never be mistaken for a real peer.
    /// </summary>
    public static IPAddress? RawPeerAddress(HttpContext httpContext)
    {
        // The PRESENCE of the key is what says the middleware ran, not the value. A server that reports
        // no peer at all (TestServer, some in-memory transports) stashes null, and that null is the
        // honest answer — falling through to Connection.RemoteIpAddress there would return the value
        // UseForwardedHeaders had just written from the client's own header.
        if (httpContext.Items.TryGetValue(RawPeerAddressItem, out var stashed))
            return stashed as IPAddress;

        // Not stashed. If the request carries a forwarded header we cannot tell whether
        // RemoteIpAddress is genuine or rewritten, so refuse to guess.
        //
        // Note this test is weaker than it looks: ForwardedHeadersMiddleware CONSUMES the header it
        // honours, so by the time an endpoint runs there may be none left to find. It catches the case
        // where the header was never trusted (and so survives) and nothing more — which is why the
        // stash above, taken ahead of that middleware, is the mechanism and this is only the backstop.
        if (httpContext.Request.Headers.ContainsKey("X-Forwarded-For")
            || httpContext.Request.Headers.ContainsKey("Forwarded"))
            return null;

        return httpContext.Connection.RemoteIpAddress;
    }

    /// <summary>
    /// Middleware that records the raw peer address, and whether the operator declared the proxy whose
    /// forwarded client IP is about to overwrite it. MUST be registered before
    /// <c>UseForwardedHeaders</c>.
    /// </summary>
    public static IApplicationBuilder UseRawPeerAddressCapture(
        this IApplicationBuilder app, bool proxyTrustDeclared = false)
        => app.Use(async (ctx, next) =>
        {
            ctx.Items[RawPeerAddressItem] = ctx.Connection.RemoteIpAddress;
            ctx.Items[ProxyTrustDeclaredItem] = proxyTrustDeclared;
            await next(ctx);
        });

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var b = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254), // link-local
            AddressFamily.InterNetworkV6 =>
                ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC, // fc00::/7 unique local
            _ => false,
        };
    }
}
