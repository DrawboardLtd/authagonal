using System.Net;
using System.Net.Sockets;

namespace Authagonal.Core.Services;

/// <summary>
/// Guards server-initiated outbound HTTP (OIDC discovery/JWKS/token/userinfo, SAML metadata,
/// provisioning callbacks) against SSRF. Rejects non-http(s) schemes and obvious internal targets
/// (loopback, link-local incl. the cloud metadata address, RFC1918/ULA literals, multicast/reserved,
/// and <c>localhost</c>/<c>.local</c>/<c>.internal</c> hostnames).
/// <para>
/// It deliberately does NOT resolve DNS, and that is no longer the gap it used to be. A URL check runs
/// where a URL is accepted — at an admin write, at registration — and resolving there would both couple
/// that path to a working resolver and prove nothing, since the answer can differ by the time anything
/// is fetched. So a hostname that resolves to an internal address is not caught here, ON PURPOSE: it is
/// caught at the socket by <see cref="SafeOutboundConnect"/>, which resolves at connect time, refuses
/// every internal address, and pins the connection to an address it actually checked — on every redirect
/// hop, because each hop is a new connection.
/// </para>
/// <para>
/// The division of labour is worth keeping straight. This function refuses a bad URL early, where the
/// error is attributable to the person who typed it. That one refuses a bad ADDRESS late, where no lie
/// about DNS can help. Neither replaces the other.
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
    /// <param name="allowlist">
    /// Internal destinations the OPERATOR configured this server to reach. Pass one ONLY where the URL
    /// itself came from operator configuration — an upstream IdP's metadata URL, a provisioning callback.
    /// Leave it null (the default) on every URL a registrant or a client supplied: there, naming an
    /// internal host is the attack rather than the deployment. See <see cref="OutboundAllowlist"/>.
    /// </param>
    public static bool IsSafe(string? url, bool allowLoopback = false, OutboundAllowlist? allowlist = null)
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

        // Asked before the rules rather than after them, so an operator-named destination is permitted
        // whichever rule would have refused it — the suffix tests below and the address test alike.
        if (allowlist?.PermitsHost(host) == true) return true;

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

    /// <summary>
    /// True when this server may originate a request to <paramref name="ip"/>.
    /// </summary>
    /// <remarks>
    /// The same judgement <see cref="IsSafe"/> applies to a literal host, exposed so
    /// <see cref="SafeOutboundConnect"/> can apply it to a RESOLVED address at connect time. One
    /// definition of "internal", used by both the URL check and the socket check — two lists would drift,
    /// and the drift would be a hole in whichever one was consulted second.
    /// </remarks>
    /// <param name="allowlist">
    /// As on <see cref="IsSafe"/>: operator-configured internal networks, and null on every
    /// registrant-supplied target.
    /// </param>
    public static bool IsAllowedAddress(
        IPAddress ip, bool allowLoopback = false, OutboundAllowlist? allowlist = null)
        => !IsBlockedIp(ip, allowLoopback) || allowlist?.PermitsAddress(ip) == true;

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
                // RFC 6598 shared address space, 100.64.0.0/10. Routed internal in real deployments and
                // omitted here while this became the LAST line of defence: 100.100.100.200 is the Alibaba
                // Cloud metadata service, 100.64.0.0/10 is the pod/service CIDR on EKS clusters using
                // secondary CIDRs, and it is the whole of Tailscale's address space. A hostname resolving
                // into it reached internal services from inside any of those networks.
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                // RFC 2544 benchmarking range, 198.18.0.0/15 — reserved, and used as internal transit.
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224,                  // multicast / reserved
            AddressFamily.InterNetworkV6 =>
                ip.IsIPv6LinkLocal
                || ip.IsIPv6Multicast
                // Deprecated by RFC 3879 and still configured, still routed, and never consulted here.
                || ip.IsIPv6SiteLocal            // fec0::/10
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
