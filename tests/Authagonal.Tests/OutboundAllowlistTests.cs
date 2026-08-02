using System.Net;
using System.Net.Http;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// The operator's answer to "which internal destinations are mine" — and the proof that it reaches only the
/// paths where the target was operator-configured.
/// </summary>
/// <remarks>
/// The SSRF guards refuse every private, loopback and link-local target, at the URL and again at the socket.
/// For a URL an attacker chose that is the whole point. But federating with an IdP reachable only over a
/// private network, or provisioning an app in the same cluster, is refused by exactly the same rule — and
/// with no way to permit it, the guard is the outage. So the distinction the allowlist encodes is WHO
/// SUPPLIED THE URL, and these tests pin both sides of it: an operator-named destination is reachable, and
/// nothing an operator names widens a registrant-supplied fetch.
/// </remarks>
public class OutboundAllowlistTests
{
    // ── Entry forms ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("idp.corp.internal", "idp.corp.internal", true)]
    [InlineData("idp.corp.internal", "IDP.CORP.INTERNAL", true)]      // DNS is case-insensitive
    [InlineData("idp.corp.internal", "idp.corp.internal.", true)]     // the trailing-dot form is the same host
    [InlineData("idp.corp.internal", "evil.corp.internal", false)]
    [InlineData("idp.corp.internal", "notidp.corp.internal", false)]
    [InlineData("*.corp.internal", "idp.corp.internal", true)]
    [InlineData("*.corp.internal", "scim.eu.corp.internal", true)]
    // The suffix keeps its leading dot, so a wildcard never matches the bare parent or a host that merely
    // ends in the same characters — "*.corp.internal" must not admit "notcorp.internal".
    [InlineData("*.corp.internal", "corp.internal", false)]
    [InlineData("*.corp.internal", "notcorp.internal", false)]
    public void HostEntriesMatchTheHostsTheyName(string entry, string host, bool permitted)
        => Assert.Equal(permitted, new OutboundAllowlist([entry]).PermitsHost(host));

    [Theory]
    [InlineData("10.4.0.0/16", "10.4.1.7", true)]
    [InlineData("10.4.0.0/16", "10.5.1.7", false)]
    [InlineData("10.4.1.7", "10.4.1.7", true)]         // a bare address is its own /32
    [InlineData("10.4.1.7", "10.4.1.8", false)]
    [InlineData("172.16.8.0/22", "172.16.11.255", true)]   // a prefix that is not byte-aligned
    [InlineData("172.16.8.0/22", "172.16.12.0", false)]
    [InlineData("fd00:1234::/48", "fd00:1234:0:5::9", true)]
    [InlineData("fd00:1234::/48", "fd00:1235::9", false)]
    [InlineData("10.0.0.0/8", "192.168.1.1", false)]
    public void NetworkEntriesMatchTheAddressesTheyCover(string entry, string address, bool permitted)
        => Assert.Equal(permitted, new OutboundAllowlist([entry]).PermitsAddress(IPAddress.Parse(address)));

    /// <summary>
    /// An IPv4 address arriving in its IPv4-mapped IPv6 form matches an IPv4 entry.
    /// </summary>
    /// <remarks>
    /// <see cref="OutboundUrl"/> collapses the mapped form before it judges an address, so an allowlist that
    /// did not would refuse what the guard would otherwise have permitted — the two would disagree about one
    /// address written two ways, which is the shape of every bug this review keeps finding.
    /// </remarks>
    [Fact]
    public void AnIPv4MappedAddressMatchesAnIPv4Entry()
        => Assert.True(new OutboundAllowlist(["10.4.0.0/16"])
            .PermitsAddress(IPAddress.Parse("::ffff:10.4.1.7")));

    /// <summary>A literal address in a URL is matched against the network entries, not the name entries.</summary>
    [Fact]
    public void ALiteralAddressHostIsMatchedAgainstTheNetworkEntries()
    {
        var allowlist = new OutboundAllowlist(["10.4.0.0/16"]);

        Assert.True(allowlist.PermitsHost("10.4.1.7"));
        Assert.False(allowlist.PermitsHost("10.5.1.7"));
    }

    [Fact]
    public void BlankEntriesAreIgnoredAndAnEmptyListPermitsNothing()
    {
        var allowlist = new OutboundAllowlist(["", "   ", null]);

        Assert.True(allowlist.IsEmpty);
        Assert.False(allowlist.PermitsHost("idp.corp.internal"));
        Assert.False(allowlist.PermitsAddress(IPAddress.Parse("10.4.1.7")));
        Assert.True(OutboundAllowlist.None.IsEmpty);
    }

    /// <summary>
    /// A malformed CIDR entry fails at startup rather than being read as a host name.
    /// </summary>
    /// <remarks>
    /// Silently treating <c>10.4.0.0/33</c> as a host name would permit exactly nothing while the operator
    /// believed a network was open, and the symptom would be a refused connection with no mention of the
    /// typo. The allowlist is built during <c>AddAuthagonal</c>, so this throw surfaces at startup.
    /// </remarks>
    [Theory]
    [InlineData("10.4.0.0/33")]
    [InlineData("10.4.0.0/-1")]
    [InlineData("fd00::/129")]
    [InlineData("not-an-address/16")]
    [InlineData("10.4.0.0/sixteen")]
    public void AMalformedNetworkEntryIsRefusedAtConstruction(string entry)
        => Assert.Throws<ArgumentException>(() => new OutboundAllowlist([entry]));

    // ── The URL layer ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An allowlisted host passes the URL check whichever of its rules would have refused the host.
    /// </summary>
    [Theory]
    [InlineData("https://idp.corp.internal/metadata")]          // the .internal suffix rule
    [InlineData("https://idp.corp.internal./metadata")]         // ... and its trailing-dot form
    public void AnAllowlistedNamePassesTheUrlCheck(string url)
    {
        var allowlist = new OutboundAllowlist(["idp.corp.internal"]);

        Assert.False(OutboundUrl.IsSafe(url), "precondition: refused without the allowlist");
        Assert.True(OutboundUrl.IsSafe(url, allowlist: allowlist));
    }

    [Fact]
    public void AnAllowlistedNetworkPassesTheUrlCheckForALiteralAddress()
    {
        var allowlist = new OutboundAllowlist(["10.4.0.0/16"]);

        Assert.False(OutboundUrl.IsSafe("https://10.4.1.7/metadata"));
        Assert.True(OutboundUrl.IsSafe("https://10.4.1.7/metadata", allowlist: allowlist));
        Assert.False(OutboundUrl.IsSafe("https://10.5.1.7/metadata", allowlist: allowlist));
    }

    /// <summary>
    /// The allowlist widens which HOSTS are reachable and nothing else. Scheme policy is untouched.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://idp.corp.internal/")]
    [InlineData("not a url")]
    public void TheAllowlistDoesNotWidenSchemePolicy(string url)
        => Assert.False(OutboundUrl.IsSafe(url, allowlist: new OutboundAllowlist(["idp.corp.internal", "10.0.0.0/8"])));

    /// <summary>
    /// A host the operator did NOT name is refused, however permissive the rest of the list is.
    /// </summary>
    [Fact]
    public void AHostOutsideTheAllowlistIsStillRefused()
    {
        var allowlist = new OutboundAllowlist(["idp.corp.internal", "10.4.0.0/16"]);

        Assert.False(OutboundUrl.IsSafe("http://169.254.169.254/latest/meta-data/", allowlist: allowlist));
        Assert.False(OutboundUrl.IsSafe("http://localhost:8080/", allowlist: allowlist));
        Assert.False(OutboundUrl.IsSafe("https://admin.svc.local/", allowlist: allowlist));
    }

    // ── The socket layer ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An operator-named host connects to whatever it resolves to, including a private address.
    /// </summary>
    /// <remarks>
    /// This is the deployment the guard was refusing: an on-premises IdP on 10.4.1.7, named in configuration
    /// by an operator. It is not a hole in the rebinding defence — that defence exists to stop an ATTACKER
    /// choosing this server's destination, and here the operator chose it by name.
    /// </remarks>
    [Fact]
    public async Task AnOperatorNamedHostConnectsToItsPrivateAddress()
    {
        var refusal = await Record.ExceptionAsync(() => ConnectAsync(
            "idp.corp.internal",
            new OutboundAllowlist(["idp.corp.internal"]),
            IPAddress.Parse("10.4.1.7")));

        // The socket attempt itself fails — nothing is listening on 10.4.1.7 in a test run — so what is
        // being asserted is WHICH failure comes back: the guard let it through to a real connect.
        Assert.DoesNotContain(
            "will not originate requests to", refusal?.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOperatorNamedNetworkConnectsToAnAddressInsideIt()
    {
        var refusal = await Record.ExceptionAsync(() => ConnectAsync(
            "idp.example.test",
            new OutboundAllowlist(["10.4.0.0/16"]),
            IPAddress.Parse("10.4.1.7")));

        Assert.DoesNotContain(
            "will not originate requests to", refusal?.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A name outside the allowlist is refused even when the allowlist is not empty.
    /// </summary>
    /// <remarks>
    /// The point of a per-destination list rather than a global switch: opening a federation target must not
    /// also open the cloud metadata service.
    /// </remarks>
    [Fact]
    public async Task AHostOutsideTheAllowlistIsStillRefusedAtTheSocket()
    {
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "logout.attacker.test",
            new OutboundAllowlist(["idp.corp.internal", "10.4.0.0/16"]),
            IPAddress.Parse("169.254.169.254")));

        Assert.Contains("will not originate requests to", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address outside the allowlisted network is refused even when the name resolving to it is not
    /// itself allowlisted — the two entry kinds do not blur into each other.
    /// </summary>
    [Fact]
    public async Task AnAddressOutsideTheAllowlistedNetworkIsRefused()
    {
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "idp.example.test",
            new OutboundAllowlist(["10.4.0.0/16"]),
            IPAddress.Parse("10.5.1.7")));

        Assert.Contains("will not originate requests to", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registrant-supplied fetch — no allowlist — is unchanged by anything an operator configured.
    /// </summary>
    /// <remarks>
    /// The guard on those paths takes no allowlist at all, so this is structural rather than a policy check:
    /// there is no argument to pass. Asserted anyway, because the whole design rests on it and a later
    /// refactor that threaded one allowlist through everything "for consistency" would pass every other test
    /// in this file.
    /// </remarks>
    [Fact]
    public async Task AStrictPathIsUnaffectedByTheOperatorAllowlist()
    {
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "idp.corp.internal", allowlist: null, IPAddress.Parse("10.4.1.7")));

        Assert.Contains("will not originate requests to", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name the operator allowlisted that resolves to NOTHING still fails as a resolution failure, not as
    /// a connection to an unchecked address.
    /// </summary>
    [Fact]
    public async Task AnOperatorNamedHostThatResolvesToNothingIsRefused()
    {
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "idp.corp.internal", new OutboundAllowlist(["idp.corp.internal"])));

        Assert.Contains("will not originate requests to", refused.Message, StringComparison.Ordinal);
    }

    private static async Task ConnectAsync(
        string host, OutboundAllowlist? allowlist, params IPAddress[] resolvesTo)
    {
        // Cancelled after a moment: an allowlisted private address that nothing answers on would otherwise
        // cost the suite a full OS connect timeout. Reaching the socket at all is the assertion.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await SafeOutboundConnect.ConnectAsync(
            host, 443, allowLoopback: false, (_, _) => Task.FromResult(resolvesTo), cts.Token, allowlist);
    }
}
