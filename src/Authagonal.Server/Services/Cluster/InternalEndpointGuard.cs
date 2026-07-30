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
        if (httpContext.Items.TryGetValue(RawPeerAddressItem, out var stashed) && stashed is IPAddress ip)
            return ip;

        // Not stashed. If the request carries a forwarded header we cannot tell whether
        // RemoteIpAddress is genuine or rewritten, so refuse to guess.
        if (httpContext.Request.Headers.ContainsKey("X-Forwarded-For")
            || httpContext.Request.Headers.ContainsKey("Forwarded"))
            return null;

        return httpContext.Connection.RemoteIpAddress;
    }

    /// <summary>
    /// Middleware that records the raw peer address. MUST be registered before
    /// <c>UseForwardedHeaders</c>.
    /// </summary>
    public static IApplicationBuilder UseRawPeerAddressCapture(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            ctx.Items[RawPeerAddressItem] = ctx.Connection.RemoteIpAddress;
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
