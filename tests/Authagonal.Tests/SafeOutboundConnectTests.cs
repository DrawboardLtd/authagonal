using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// #107 / #186 — the half of the SSRF guard a URL check cannot do: refusing the ADDRESS a name resolves to.
/// </summary>
/// <remarks>
/// <see cref="OutboundUrl.IsSafe"/> was, and remains, a text check. Where the host is a name it can only
/// say "that looks like a name", and the owner of the name decides what it becomes —
/// <c>logout.attacker.test</c> passes every literal-IP and suffix rule and answers with
/// <c>169.254.169.254</c>. Both findings' write-site guards were real and both left this untouched.
/// <para>
/// The resolver is injected so the attack is reproducible without DNS: these tests state exactly what a
/// hostile name returns and assert the connection is refused before a socket is opened. A test that used
/// real resolution could only assert against whatever the box's resolver happens to say today.
/// </para>
/// </remarks>
public class SafeOutboundConnectTests
{
    /// <summary>
    /// The address rule the callback applies, stated per address family and shared with the URL check.
    /// </summary>
    [Theory]
    [InlineData("169.254.169.254", false)]   // cloud metadata
    [InlineData("127.0.0.1", false)]         // loopback
    [InlineData("10.0.0.5", false)]          // RFC1918
    [InlineData("172.16.4.4", false)]        // RFC1918
    [InlineData("192.168.1.1", false)]       // RFC1918
    [InlineData("0.0.0.0", false)]           // unspecified
    [InlineData("::1", false)]               // IPv6 loopback
    [InlineData("::", false)]                // IPv6 unspecified
    [InlineData("fd00::1", false)]           // IPv6 ULA
    [InlineData("fe80::1", false)]           // IPv6 link-local
    // RFC 6598 shared address space. Omitted while this was the last line of defence, and it is not an
    // exotic range: 100.100.100.200 is the Alibaba Cloud metadata service, 100.64.0.0/10 is the pod CIDR
    // on EKS clusters using secondary CIDRs, and it is all of Tailscale.
    [InlineData("100.64.0.1", false)]
    [InlineData("100.100.100.200", false)]   // Alibaba Cloud metadata
    [InlineData("100.127.255.255", false)]
    [InlineData("100.63.255.255", true)]     // just below the range — still public
    [InlineData("100.128.0.0", true)]        // just above it
    [InlineData("198.18.0.1", false)]        // RFC 2544 benchmarking / internal transit
    [InlineData("198.19.255.255", false)]
    [InlineData("198.20.0.1", true)]         // just above it
    [InlineData("fec0::1", false)]           // IPv6 site-local, deprecated but still routed
    [InlineData("93.184.216.34", true)]      // ordinary public address
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void TheAddressRuleMatchesTheUrlRule(string address, bool allowed)
        => Assert.Equal(allowed, OutboundUrl.IsAllowedAddress(IPAddress.Parse(address)));

    [Fact]
    public void LoopbackIsAllowedOnlyWhenExplicitlyPermitted()
    {
        Assert.False(OutboundUrl.IsAllowedAddress(IPAddress.Loopback));
        Assert.True(OutboundUrl.IsAllowedAddress(IPAddress.Loopback, allowLoopback: true));
        // The exception is loopback and nothing else: it exists for a development relying party on the
        // user's own machine, not as a general relaxation.
        Assert.False(OutboundUrl.IsAllowedAddress(IPAddress.Parse("10.0.0.5"), allowLoopback: true));
    }

    /// <summary>
    /// A name that passes the URL check and resolves to the metadata service is refused at connect.
    /// </summary>
    /// <remarks>
    /// This is the finding, end to end: the URL is one <see cref="OutboundUrl.IsSafe"/> accepts — https,
    /// a perfectly ordinary hostname, no internal suffix — and the connection still does not happen.
    /// </remarks>
    [Fact]
    public async Task ANameResolvingToAnInternalAddressIsRefused()
    {
        Assert.True(OutboundUrl.IsSafe("https://logout.attacker.test/backchannel"),
            "precondition: the URL check accepts this, which is why the socket check has to exist");

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ConnectAsync("logout.attacker.test", IPAddress.Parse("169.254.169.254")));

        Assert.Contains("will not originate requests to", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name answering with a public AND an internal address is refused, not partially honoured.
    /// </summary>
    /// <remarks>
    /// Rebinding compressed into one response. Accepting it because one entry looked fine would leave the
    /// choice of address to whatever order the resolver returned — which is the attacker's to influence.
    /// </remarks>
    [Fact]
    public async Task ANameAnsweringWithBothPublicAndInternalAddressesIsRefused()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "split.attacker.test",
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("10.0.0.5")));
    }

    [Fact]
    public async Task ALiteralInternalAddressIsRefusedWithoutResolving()
    {
        var resolverCalled = false;

        await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "169.254.169.254",
            resolver: (_, _) => { resolverCalled = true; return Task.FromResult<IPAddress[]>([]); }));

        Assert.False(resolverCalled, "a literal address needs no lookup");
    }

    /// <summary>
    /// A name resolving only to public addresses gets as far as a real connection attempt.
    /// </summary>
    /// <remarks>
    /// The guard has to be non-vacuous in the permissive direction too, or "refuse everything" would pass
    /// every test above. Connecting to a documentation address fails at the socket, not at the guard — the
    /// distinction being asserted is which error comes back.
    /// </remarks>
    [Fact]
    public async Task APublicNameIsNotRefusedByTheGuard()
    {
        // Cancelled after a moment rather than left to the OS connect timeout: 203.0.113.0/24 is
        // TEST-NET-3 and black-holes, so an unbounded attempt costs the suite minutes. Reaching the
        // socket at all is the assertion — the guard let it through.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var error = await Record.ExceptionAsync(() => ConnectAsync(
            "rp.example.test",
            (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.9")]),
            cts.Token));

        Assert.NotNull(error);
        Assert.DoesNotContain("will not originate requests to", error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resolver that answers with an empty array — rather than throwing — is also a resolution failure.
    /// </summary>
    /// <remarks>
    /// It failed closed, correctly, but reported the policy refusal: an operator chasing a DNS problem was
    /// told their host resolved to a loopback or private address. The combined length test cannot distinguish
    /// the cases — zero allowed out of zero candidates reads exactly like zero out of five — so the empty
    /// answer has to be caught before it.
    /// </remarks>
    [Fact]
    public async Task ANameResolvingToNoAddressesFailsAsResolutionNotAsRefusal()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "empty.attacker.test",
            resolver: (_, _) => Task.FromResult<IPAddress[]>([])));

        Assert.Contains("Could not resolve", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("will not originate requests to", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled connect surfaces AS cancellation, not as a connection refusal.
    /// </summary>
    /// <remarks>
    /// The retry loop caught every exception, so a cancelled connect was retried against each remaining
    /// address — each throwing immediately on the already-cancelled token — and then reported as
    /// <c>HttpRequestException("Could not connect")</c>. Inside <c>HttpClient</c> that was partly repaired by
    /// chance, because the connection pool rewraps when the token is already cancelled; the public surface
    /// these tests use got the wrong type, and the permissive-direction test above passed BECAUSE of the
    /// swallowing rather than in spite of it.
    /// </remarks>
    [Fact]
    public async Task ACancelledConnectSurfacesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectAsync(
            "rp.example.test",
            (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.9"), IPAddress.Parse("203.0.113.10")]),
            cts.Token));
    }

    [Fact]
    public async Task AnUnresolvableNameFailsAsResolutionNotAsRefusal()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(
            "nx.attacker.test",
            resolver: (_, _) => throw new SocketException((int)SocketError.HostNotFound)));

        Assert.Contains("Could not resolve", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drives the callback's decision without a live <c>SocketsHttpConnectionContext</c>, which has no
    /// public constructor. Reaching the same guard through the same delegate the handler is given.
    /// </summary>
    private static async Task ConnectAsync(
        string host,
        params IPAddress[] resolvesTo)
        => await ConnectAsync(host, (_, _) => Task.FromResult(resolvesTo));

    private static async Task ConnectAsync(
        string host,
        SafeOutboundConnect.HostResolver resolver,
        CancellationToken ct = default)
    {
        // The callback's whole contract is (host, port) → validated stream, so exercising it through a
        // small shim that supplies those is exercising the shipped code, not a copy of it.
        await SafeOutboundConnect.ConnectAsync(host, 443, allowLoopback: false, resolver, ct);
    }
}
