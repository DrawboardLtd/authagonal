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
    /// The one key every per-source quota in this server is keyed on: the forwarded client IP when the
    /// operator declared the proxy that supplies it, and the raw peer address otherwise.
    /// </summary>
    /// <remarks>
    /// <c>Connection.RemoteIpAddress</c> is not that key. With no proxy declared, <c>UseAuthagonal</c>
    /// still honours <c>X-Forwarded-For</c> from the loopback/private ranges — a deliberate guess that
    /// beats the framework's honour-it-from-anybody default for logging — so a caller whose immediate peer
    /// sits in those ranges and behind which nothing appends its own XFF (an L4 load balancer, a docker
    /// bridge, pod-to-pod, or the attacker being on that network) writes the value the rewrite lands on. A
    /// limiter keyed on it hands that caller a fresh bucket per request, which is the same as having no
    /// limiter: registration flooding and DCR client-record flooding become unbounded from one host.
    /// <para>
    /// The declared case is different in kind, not in degree: there the header can only have been set by
    /// the named proxy, so it is evidence rather than a guess. That is the same line <c>UseAuthagonal</c>
    /// already draws for <c>X-Forwarded-Proto</c>, and the reason the fallback ranges are documented as
    /// never load-bearing for a security decision — a quota is one.
    /// </para>
    ///
    /// <para>
    /// <b>What the undeclared case costs, and why it is not fixed here.</b> Behind an L7 proxy with nothing
    /// declared, the raw peer is that proxy for every request, so every client in the world shares one
    /// bucket and any anonymous caller can spend the whole budget for everybody. That is a real
    /// availability defect and it is tempting to fix it by folding the forwarded value back into the key —
    /// which reopens the bypass above exactly, because behind an L4 path the forwarded value is the
    /// attacker's own header. The two requirements are in genuine conflict and NOTHING OBSERVABLE
    /// DISTINGUISHES THEM: whether the rightmost forwarded hop was written by a proxy or by the caller is
    /// precisely what the operator's declaration states and what the server cannot otherwise know. So the
    /// resolution is configuration, not code — <c>UseAuthagonal</c> warns at startup, naming this
    /// consequence, and a declared proxy makes the quota per-client and correct. Do not "fix" this by
    /// re-keying; fix the deployment.
    /// </para>
    ///
    /// <para>
    /// Every source-keyed limiter must come through here. Three of them did not, and each was wrong in its
    /// own way: login and the SAML ACS read <c>RawPeerAddress</c> unconditionally, so they collapsed into
    /// one global bucket even for an operator who HAD declared their proxy — a 30-per-5-minutes login
    /// budget for an entire deployment, spendable by anyone — and forgot-password read
    /// <c>Connection.RemoteIpAddress</c> unconditionally, so at that site the bypass above was never closed
    /// at all. Sibling call sites of one fix, which is why there is now one function and no alternatives.
    /// </para>
    /// </remarks>
    public static string SourceQuotaKey(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ProxyTrustDeclaredItem, out var declared) && declared is true)
            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Undeclared: fall back to the peer we actually observed. See the remarks — this is the shared
        // bucket, and declaring the proxy is what resolves it.
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
