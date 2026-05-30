using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Authorizes requests to internal-only endpoints (the <c>/_internal/*</c> routes: cluster gossip
/// and back-channel logout). These must never be invokable by external callers.
/// <para>
/// When a shared secret is configured (<c>Cluster:Secret</c>) the caller must present it in the
/// <c>X-Cluster-Secret</c> header (compared in constant time). When no secret is configured the
/// endpoint is only reachable from inside the cluster: after <c>UseForwardedHeaders</c> resolves the
/// real client address, an external request carries a public IP and is rejected, while pod-to-pod
/// gossip uses loopback/private (RFC1918 / link-local / ULA) addresses and is allowed.
/// </para>
/// </summary>
public static class InternalEndpointGuard
{
    public const string SecretHeader = "X-Cluster-Secret";

    public static bool IsAuthorized(HttpContext httpContext, string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            var provided = httpContext.Request.Headers[SecretHeader].ToString();
            return !string.IsNullOrEmpty(provided) &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(secret));
        }

        var ip = httpContext.Connection.RemoteIpAddress;
        return ip is not null && (IPAddress.IsLoopback(ip) || IsPrivate(ip));
    }

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
