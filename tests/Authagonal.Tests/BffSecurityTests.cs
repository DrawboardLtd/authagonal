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

    // Exchange routes: segment-prefix match with exactly one {param} capture; first match wins;
    // no capture (missing segment / literal mismatch / no placeholder) → no exchange.
    [Theory]
    [InlineData("/projects/123/annotations", "/projects/{project_id}", true, "project_id", "123")]
    [InlineData("/projects/123", "/projects/{project_id}", true, "project_id", "123")]
    [InlineData("/projects", "/projects/{project_id}", false, null, null)]
    [InlineData("/workspaces/ws-9/x", "/workspaces/{workspace_id}", true, "workspace_id", "ws-9")]
    [InlineData("/other/123", "/projects/{project_id}", false, null, null)]
    [InlineData("/PROJECTS/123", "/projects/{project_id}", true, "project_id", "123")] // path casing tolerant
    public void TryMatchExchangeRoute_captures_binding_segment(
        string apiPath, string pattern, bool expected, string? expectedName, string? expectedValue)
    {
        var routes = new[] { new BffExchangeRoute { PathPattern = pattern } };
        var matched = BffProxy.TryMatchExchangeRoute(routes, apiPath, out var name, out var value);
        Assert.Equal(expected, matched);
        if (expected)
        {
            Assert.Equal(expectedName, name);
            Assert.Equal(expectedValue, value);
        }
    }

    // {param:guid} constraint: broad version-prefixed patterns must only bind GUID segments, so
    // literal-segment routes ("/v1/user/profile") never demand an exchange.
    [Theory]
    [InlineData("/v2/6f9619ff-8b86-d011-b42d-00c04fc964ff/document", true, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("/v1/user/profile", false, null)]
    [InlineData("/v2/organizations/audit-log", false, null)]
    [InlineData("/v3/not-a-guid/document", false, null)]
    public void TryMatchExchangeRoute_guid_constraint_gates_binding(string apiPath, bool expected, string? expectedValue)
    {
        var routes = new[] { new BffExchangeRoute { PathPattern = "/{apiver}/{project_id:guid}" } };
        var matched = BffProxy.TryMatchExchangeRoute(routes, apiPath, out var name, out var value);
        Assert.Equal(expected, matched);
        if (expected)
        {
            Assert.Equal("project_id", name);
            Assert.Equal(expectedValue, value);
        }
    }

    /// <summary>
    /// An unknown route constraint is refused, not treated as a non-match.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite, and the matcher's comment agreed with it: "unknown constraint:
    /// never match rather than silently over-match". The two directions are not symmetric. A route that does
    /// not match sends <c>ProxyAsync</c> down the else-branch, which attaches the session's PRIMARY,
    /// un-downscoped access token instead of the RFC 8693 context-bound one the route was configured to
    /// require — so "never match" was the fail-OPEN direction for the only thing this route does.
    /// Over-matching would have demanded an exchange that was not needed and cost a 403.
    /// <para>
    /// A typo in a constraint name is now a startup failure (see
    /// <c>AuthagonalBffOptions.ValidateExchangeRoutes</c>); this is the runtime backstop.
    /// </para>
    /// </remarks>
    [Fact]
    public void TryMatchExchangeRoute_unknown_constraint_is_refused()
    {
        var routes = new[] { new BffExchangeRoute { PathPattern = "/{apiver}/{project_id:int}" } };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BffProxy.TryMatchExchangeRoute(routes, "/v1/123/x", out _, out _));

        Assert.Contains("int", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A pattern with an unknown constraint does not survive startup validation.</summary>
    [Fact]
    public void Options_reject_an_unknown_route_constraint_at_startup()
    {
        var options = new AuthagonalBffOptions
        {
            Authority = "https://idp.test",
            ClientId = "bff",
            ClientSecret = "secret",
            ExchangeRoutes = [new BffExchangeRoute { PathPattern = "/{apiver}/{project_id:int}" }],
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("int", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a supported constraint still matches, and still validates.</summary>
    [Fact]
    public void A_supported_constraint_still_matches()
    {
        var routes = new[] { new BffExchangeRoute { PathPattern = "/{apiver}/{project_id:guid}" } };
        var id = Guid.NewGuid().ToString();

        Assert.True(BffProxy.TryMatchExchangeRoute(routes, $"/v1/{id}", out var name, out var value));
        Assert.Equal("project_id", name);
        Assert.Equal(id, value);
    }

    [Fact]
    public void TryMatchExchangeRoute_first_matching_route_wins()
    {
        var routes = new[]
        {
            new BffExchangeRoute { PathPattern = "/projects/{project_id}" },
            new BffExchangeRoute { PathPattern = "/{anything}" },
        };
        Assert.True(BffProxy.TryMatchExchangeRoute(routes, "/projects/p1/files", out var name, out var value));
        Assert.Equal("project_id", name);
        Assert.Equal("p1", value);
    }

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
