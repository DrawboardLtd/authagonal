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
    // The unspecified address. The IPv4 arm had blocked 0.0.0.0 since it was written and the IPv6 arm had
    // no counterpart, so the guard refused one spelling of "the local host" and permitted the other.
    [InlineData("http://[::]/")]
    [InlineData("http://[0:0:0:0:0:0:0:0]/")]                         // the same address, written out
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

    // -----------------------------------------------------------------------
    // Trailing-dot bypass (#55)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A trailing dot is a fully-qualified form that DNS resolves exactly like the bare name, but
    /// <c>IPAddress.TryParse</c> rejects it — so a literal internal address fell past the literal-IP branch
    /// to the permissive default, and "localhost." matched neither the equality test nor the ".local"
    /// suffix. One character defeated the entire guard, including the cloud metadata address.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254./latest/meta-data/")]  // cloud metadata, FQDN form
    [InlineData("https://169.254.169.254./")]
    [InlineData("http://127.0.0.1./")]
    [InlineData("http://10.0.0.5./")]
    [InlineData("http://192.168.1.1./")]
    [InlineData("http://172.16.0.1./")]
    [InlineData("http://localhost./")]
    [InlineData("http://localhost../")]                        // repeated dots
    [InlineData("http://foo.internal./")]
    [InlineData("http://foo.local./")]
    public void TrailingDotHosts_AreBlocked(string url)
        => Assert.False(OutboundUrlValidator.IsSafe(url), url);

    /// <summary>A legitimate external host with a trailing dot must still be allowed — the fix normalises,
    /// it does not blanket-reject the FQDN form.</summary>
    [Theory]
    [InlineData("https://idp.example.com./metadata")]
    [InlineData("https://login.microsoftonline.com./common/v2.0/.well-known/openid-configuration")]
    public void TrailingDotExternalHosts_AreAllowed(string url)
        => Assert.True(OutboundUrlValidator.IsSafe(url), url);

    // -----------------------------------------------------------------------
    // The loopback opt-in (#186)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loopback is refused by default and permitted only where a caller opts in — which is the OIDC
    /// logout DELIVERY path, where a relying party on localhost is a supported development setup and
    /// the write-time guards have already refused anything new. Everywhere a URL is ACCEPTED, loopback
    /// stays blocked.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:5000/logout")]
    [InlineData("http://127.5.5.5/logout")]
    [InlineData("http://localhost:5000/logout")]
    [InlineData("http://[::1]:5000/logout")]
    public void Loopback_IsBlockedByDefault_AndPermittedOnlyOnOptIn(string url)
    {
        Assert.False(Authagonal.Core.Services.OutboundUrl.IsSafe(url), url);
        Assert.True(Authagonal.Core.Services.OutboundUrl.IsSafe(url, allowLoopback: true), url);
    }

    /// <summary>
    /// The opt-in is loopback ONLY. Everything the finding is actually about — the cloud metadata
    /// address, RFC1918, internal DNS — stays blocked on both sides, or the delivery-time check would
    /// be decoration.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/logout")]
    [InlineData("http://192.168.1.1/logout")]
    [InlineData("http://172.16.0.1/logout")]
    [InlineData("https://vault.internal/logout")]
    [InlineData("https://svc.local/logout")]
    public void LoopbackOptIn_DoesNotOpenInternalTargets(string url)
        => Assert.False(Authagonal.Core.Services.OutboundUrl.IsSafe(url, allowLoopback: true), url);
}
