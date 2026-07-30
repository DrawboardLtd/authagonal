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
    public static bool IsSafe(string? url)
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

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IPAddress.TryParse(host, out var ip))
            return !IsBlockedIp(ip);

        return true;
    }

    private static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

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
                ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC, // ULA fc00::/7
            _ => true,
        };
    }
}
