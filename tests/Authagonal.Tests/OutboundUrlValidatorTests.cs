using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// SSRF guard for server-initiated outbound HTTP (OIDC discovery/JWKS, SAML metadata, provisioning
/// callbacks). The validator is deliberately DNS-free, so these tests use literal IPs and the
/// blocked hostname suffixes — the exact surface the class claims to cover.
/// </summary>
public class OutboundUrlValidatorTests
{
    // ── Malformed / non-http(s) ──────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void MalformedOrEmpty_IsBlocked(string? url)
        => Assert.False(OutboundUrlValidator.IsSafe(url));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/pub/file.txt")]
    [InlineData("gopher://example.com/")]
    [InlineData("ldap://example.com/")]
    [InlineData("javascript:alert(1)")]
    public void NonHttpScheme_IsBlocked(string url)
        => Assert.False(OutboundUrlValidator.IsSafe(url));

    // ── Cloud metadata + loopback ────────────────────────────────────

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]          // AWS/Azure metadata
    [InlineData("https://169.254.169.254/metadata/instance")]
    [InlineData("http://169.254.0.1/")]                               // any link-local
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8200/v1/sys/health")]               // port doesn't help
    [InlineData("https://127.255.255.254/")]                          // whole 127/8 range
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST/")]                                 // case-insensitive
    [InlineData("http://api.localhost/")]
    [InlineData("http://vault.local/")]
    [InlineData("http://db.internal/")]
    public void MetadataLoopbackAndInternalHosts_AreBlocked(string url)
        => Assert.False(OutboundUrlValidator.IsSafe(url));

    // ── RFC1918 + other reserved IPv4 ────────────────────────────────

    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("https://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://224.0.0.1/")]                                 // multicast
    [InlineData("http://255.255.255.255/")]                           // broadcast
    public void PrivateAndReservedIPv4_AreBlocked(string url)
        => Assert.False(OutboundUrlValidator.IsSafe(url));

    // ── IPv6 internal ranges ─────────────────────────────────────────

    [Theory]
    [InlineData("http://[::1]/")]                                     // loopback
    [InlineData("http://[fe80::1]/")]                                 // link-local
    [InlineData("http://[fc00::1]/")]                                 // ULA fc00::/7
    [InlineData("http://[fd12:3456:789a::1]/")]                       // ULA fd00::/8 half
    [InlineData("http://[ff02::1]/")]                                 // multicast
    [InlineData("http://[::ffff:127.0.0.1]/")]                        // IPv4-mapped loopback
    [InlineData("http://[::ffff:10.0.0.1]/")]                         // IPv4-mapped RFC1918
    [InlineData("http://[::ffff:169.254.169.254]/")]                  // IPv4-mapped metadata
    public void InternalIPv6_IsBlocked(string url)
        => Assert.False(OutboundUrlValidator.IsSafe(url));

    // ── Legitimate public targets ────────────────────────────────────

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/metadata")]                       // plain http allowed (scheme policy is caller's)
    [InlineData("https://login.microsoftonline.com/tenant/.well-known/openid-configuration")]
    [InlineData("https://idp.example.com:8443/saml/metadata")]
    [InlineData("https://8.8.8.8/")]                                  // public IP literal
    [InlineData("https://93.184.216.34/path?query=1")]
    [InlineData("http://[2606:4700:4700::1111]/")]                    // public IPv6 literal
    [InlineData("https://internal.example.com/")]                     // ".internal" is a suffix match, not a substring match
    [InlineData("https://mylocal.example.com/")]
    public void PublicUrls_AreAllowed(string url)
        => Assert.True(OutboundUrlValidator.IsSafe(url));

    // ── Boundary neighbours of the blocked ranges stay open ─────────

    [Theory]
    [InlineData("http://11.0.0.1/")]                                  // just above 10/8
    [InlineData("http://172.15.255.255/")]                            // just below 172.16/12
    [InlineData("http://172.32.0.1/")]                                // just above 172.31
    [InlineData("http://192.167.1.1/")]                               // just below 192.168/16
    [InlineData("http://192.169.1.1/")]                               // just above 192.168/16
    [InlineData("http://169.253.255.255/")]                           // just below link-local
    [InlineData("http://223.255.255.255/")]                           // just below multicast
    public void PublicIPv4RangeBoundaries_AreAllowed(string url)
        => Assert.True(OutboundUrlValidator.IsSafe(url));
}
