using Authagonal.Bff;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>Unit coverage for the BFF's security-relevant pure helpers (previously untested — the BFF
/// had no test project). Covers the H3 open-redirect fix, the L1 prefix-boundary fix, and the M11
/// single-use ws-ticket redemption.</summary>
public class BffSecurityTests
{
    private static AuthagonalBffOptions Opts(params string[] allowlist)
    {
        var o = new AuthagonalBffOptions();
        foreach (var a in allowlist) o.ReturnUrlAllowlist.Add(a);
        return o;
    }

    // H3: SanitizeReturnUrl must reject protocol-relative ("//") AND backslash-tricked ("/\") targets —
    // browsers normalize '\' to '/', turning "/\evil.com" into an off-site protocol-relative redirect.
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/a/b?x=1", "/a/b?x=1")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]        // the H3 fix — backslash after the leading slash
    [InlineData("/\\/evil.com", "/")]
    [InlineData("https://evil.example/x", "/")]   // absolute, not allow-listed
    public void SanitizeReturnUrl_rejects_offsite(string? input, string expected)
        => Assert.Equal(expected, BffEndpoints.SanitizeReturnUrl(input, Opts()));

    [Fact]
    public void SanitizeReturnUrl_allows_only_registered_origins()
    {
        var o = Opts("https://app.example");
        Assert.Equal("https://app.example/landing", BffEndpoints.SanitizeReturnUrl("https://app.example/landing", o));
        Assert.Equal("/", BffEndpoints.SanitizeReturnUrl("https://other.example/landing", o));
    }

    // L1: prefix matching must respect segment boundaries so "/id" can't capture "/identity/...".
    [Theory]
    [InlineData("/identity/x", "/id", false)]
    [InlineData("/id/x", "/id", true)]
    [InlineData("/id", "/id", true)]
    [InlineData("/orders/1", "/orders", true)]
    [InlineData("/ordersX", "/orders", false)]
    [InlineData("/api/v1/x", "/api/", true)]   // prefix ending in '/'
    public void PrefixMatches_respects_segment_boundary(string path, string prefix, bool expected)
        => Assert.Equal(expected, BffProxy.PrefixMatches(path, prefix));

    // M11: a ws-ticket redeems exactly once, then is gone.
    [Fact]
    public async Task TryRedeemWsTicket_is_single_use()
    {
        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        const string ticket = "abc123";
        await cache.SetStringAsync(BffEndpoints.WsTicketKey(ticket), "the-access-token");

        Assert.Equal("the-access-token", await BffEndpoints.TryRedeemWsTicketAsync(cache, ticket));
        Assert.Null(await BffEndpoints.TryRedeemWsTicketAsync(cache, ticket));    // already consumed
        Assert.Null(await BffEndpoints.TryRedeemWsTicketAsync(cache, "unknown"));
    }
}
