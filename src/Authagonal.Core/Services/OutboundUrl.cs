using System.Net;
using System.Net.Sockets;

namespace Authagonal.Core.Services;

/// <summary>
/// Guards server-initiated outbound HTTP (OIDC discovery/JWKS/token/userinfo, SAML metadata,
/// provisioning callbacks) against SSRF. Rejects non-http(s) schemes and obvious internal targets
/// (loopback, link-local incl. the cloud metadata address, RFC1918/ULA literals, multicast/reserved,
/// and <c>localhost</c>/<c>.local</c>/<c>.internal</c> hostnames).
/// <para>
/// It does not resolve DNS — that would block legitimate external hosts in offline/test environments
/// and add resolve-time coupling — so a hostname that resolves to an internal address is not caught
/// here. The high-value oracles (literal metadata/loopback/private IPs and the common internal
/// hostnames) are blocked, which is the realistic SSRF surface for these admin-configured URLs.
/// </para>
/// </summary>
public static class OutboundUrl
{
    /// <param name="allowLoopback">
    /// Permit <c>localhost</c> / 127.0.0.0/8 / ::1. Off everywhere a URL is being ACCEPTED — an
    /// operator or a registrant naming loopback has either made a mistake or is probing this server's
    /// own admin surface. On only at DELIVERY time for the OIDC logout URIs, where a relying party on
    /// localhost is a supported development configuration and the write-time guards have already
    /// refused anything new: the delivery check exists to catch rows that predate those guards or come
    /// from an embedding host's own store, and re-litigating loopback there would break a flow that
    /// nothing in the write path is even trying to stop.
    /// </param>
    public static bool IsSafe(string? url, bool allowLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Strip trailing dots BEFORE any comparison. "169.254.169.254." is a fully-qualified form that DNS
        // resolves exactly like the bare address, but IPAddress.TryParse rejects it — so it fell past the
        // literal-IP branch to the permissive default. "localhost." likewise matched neither the equality
        // nor the ".local" suffix test. One trailing character defeated the whole guard.
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Length == 0) return false;

        var isLoopbackName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

        if ((isLoopbackName && !allowLoopback)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IPAddress.TryParse(host, out var ip))
            return !IsBlockedIp(ip, allowLoopback);

        return true;
    }

    private static bool IsBlockedIp(IPAddress ip, bool allowLoopback)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return !allowLoopback;

        var b = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)  // link-local, incl. 169.254.169.254 metadata
                || b[0] >= 224,                  // multicast / reserved
            AddressFamily.InterNetworkV6 =>
                ip.IsIPv6LinkLocal
                || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC       // ULA fc00::/7
                // The unspecified address, :: — the IPv6 sibling of the `b[0] == 0` rule on the IPv4 arm,
                // which was there and had no counterpart here. A connect() to :: goes to the local host on
                // every mainstream stack, exactly as 0.0.0.0 does, so leaving it out meant the guard
                // refused `http://0.0.0.0/` and permitted `http://[::]/` — the same target by the other
                // family. Equals rather than a byte test because IPAddress.IPv6Any is the canonical value.
                || ip.Equals(IPAddress.IPv6Any),
            _ => true,
        };
    }
}
