using System.Net;
using System.Text.RegularExpressions;
using Authagonal.Server.Services.Cluster;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Every per-source quota must be keyed through InternalEndpointGuard.SourceQuotaKey, and the reason is
// that the three call sites which decided for themselves were each wrong in a different way.
//
// The forged-address fix (#46) established the rule: with no declared proxy, Connection.RemoteIpAddress is
// whatever X-Forwarded-For said, so a limiter keyed on it hands the caller a fresh bucket per request. It
// was applied to registration and DCR. It was not applied to:
//
//   - login, which read RawPeerAddress directly. That is the TCP peer ALWAYS, so behind any reverse
//     proxy — declared or not, correctly configured or not — every client in the deployment shared one
//     bucket. Thirty failed logins from anyone bought a 429 for every user, repeatable forever.
//   - the SAML ACS, the same way, 60/min shared per connection.
//   - forgot-password, which read Connection.RemoteIpAddress directly, so #46's bypass was simply never
//     closed there — and that cap is the only bound on walking an address list through this server's own
//     verified sending domain.
//
// Sibling call sites of one fix. No behavioural test can catch the next one, because the correct and the
// broken key produce identical results in a test host with no forwarded-headers pipeline — which is
// exactly how the existing end-to-end tests in ForgedClientAddressTests pass while being blind to this.
// So the class gets a convention test.
// -------------------------------------------------------------------------------------------------
public sealed class SourceQuotaKeyConventionTests
{
    /// <summary>
    /// Rate-limit keys permitted to derive a source address any other way, each with the reason. Empty by
    /// design. An entry has to explain why that site's notion of "source" is not the one every other
    /// quota uses.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    /// <summary>
    /// A rate-limit key built from a raw address expression rather than from <c>SourceQuotaKey</c>.
    /// </summary>
    /// <remarks>
    /// Matches the assignment that feeds a limiter key, not the limiter call, because the two are usually
    /// on different lines: what is being looked for is a local initialised from
    /// <c>RawPeerAddress(...)</c> or <c>Connection.RemoteIpAddress</c>. Turnstile also reads
    /// <c>Connection.RemoteIpAddress</c> and legitimately must (the challenge is matched against the
    /// address that solved it), so the match is narrowed to lines that look like they are naming a quota
    /// source — an identifier containing ip, peer, addr or source.
    /// </remarks>
    private static readonly Regex QuotaSourceFromRawAddress = new(
        @"\b(?:var|string)\s+\w*(?:[Ii]p|[Pp]eer|[Aa]ddr|[Ss]ource)\w*\s*=\s*[^;]*"
        + @"(?:RawPeerAddress\s*\(|Connection\.RemoteIpAddress)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryPerSourceQuotaKeyComesFromSourceQuotaKey()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        Assert.True(Directory.Exists(src), $"Expected the source tree at '{src}'.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (Allowed.ContainsKey(relative)) continue;

            // The guard itself is where both values legitimately come from — it is the implementation of
            // the rule, not a violation of it.
            if (relative.EndsWith("Services/Cluster/InternalEndpointGuard.cs", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                if (QuotaSourceFromRawAddress.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}  {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A per-source quota keyed on RawPeerAddress collapses every client behind a reverse proxy into "
            + "one bucket, so any anonymous caller can spend the whole budget for everybody; keyed on "
            + "Connection.RemoteIpAddress it is mintable per request by varying X-Forwarded-For. "
            + "InternalEndpointGuard.SourceQuotaKey is the one place that decides between them, on whether "
            + "the operator declared their proxy. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ── The property the collapse violated ───────────────────────────

    /// <summary>
    /// With a declared proxy, two clients arriving through the SAME proxy get DIFFERENT keys.
    /// </summary>
    /// <remarks>
    /// This is the assertion the collapsed sites failed, and it is the one that matters: a quota which
    /// cannot tell two clients apart is not a quota, it is a shared fuse that the first caller to arrive
    /// gets to blow for everyone. Stated as a distinctness property rather than as an expected string so it
    /// keeps meaning something if the key format changes.
    /// </remarks>
    [Fact]
    public void WithADeclaredProxy_TwoClientsThroughOneProxyGetDistinctKeys()
    {
        var first = SourceQuotaKey(rawPeer: "10.4.0.9", forwarded: "203.0.113.9", proxyDeclared: true);
        var second = SourceQuotaKey(rawPeer: "10.4.0.9", forwarded: "198.51.100.7", proxyDeclared: true);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// With NO declared proxy, those same two clients share a key — deliberately, and documented here so
    /// that nobody "fixes" it without reading why.
    /// </summary>
    /// <remarks>
    /// It is tempting to fold the forwarded value in and make this per-client. That reopens #46 exactly:
    /// behind an L4 path the forwarded value IS the caller's own header, so mixing it in restores the fresh
    /// bucket per request that made the cap meaningless. Whether the rightmost forwarded hop was written by
    /// a proxy or by the caller is precisely what the declaration states and what nothing else can reveal,
    /// so the shared bucket is the honest answer to an unanswerable question. <c>UseAuthagonal</c> warns
    /// about it at startup, naming this consequence; declaring the proxy is the fix.
    /// </remarks>
    [Fact]
    public void WithNoDeclaredProxy_TheKeyIsSharedAndThatIsTheDocumentedTradeoff()
    {
        var first = SourceQuotaKey(rawPeer: "10.4.0.9", forwarded: "203.0.113.9", proxyDeclared: false);
        var second = SourceQuotaKey(rawPeer: "10.4.0.9", forwarded: "198.51.100.7", proxyDeclared: false);

        Assert.Equal(first, second);
        // And it is the peer, not either forwarded value — the caller cannot influence it at all.
        Assert.Equal("10.4.0.9", first);
    }

    /// <summary>
    /// Direct callers with no proxy in front are distinguished whether or not anything is declared.
    /// </summary>
    [Fact]
    public void WithNoProxyAtAll_DistinctCallersAreAlwaysDistinct()
    {
        foreach (var declared in new[] { true, false })
        {
            var first = SourceQuotaKey("203.0.113.9", "203.0.113.9", declared);
            var second = SourceQuotaKey("198.51.100.7", "198.51.100.7", declared);

            Assert.NotEqual(first, second);
        }
    }

    private static string SourceQuotaKey(string rawPeer, string forwarded, bool proxyDeclared)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[InternalEndpointGuard.RawPeerAddressItem] = IPAddress.Parse(rawPeer);
        ctx.Items[InternalEndpointGuard.ProxyTrustDeclaredItem] = proxyDeclared;
        // What UseForwardedHeaders leaves behind: the forwarded client IP when it honoured the header.
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(forwarded);

        return InternalEndpointGuard.SourceQuotaKey(ctx);
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution file. Fails loudly rather
    /// than skipping: a lint that quietly finds nothing to scan is worse than no lint.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
