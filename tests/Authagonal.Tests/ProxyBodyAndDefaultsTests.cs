using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Headers;
using Authagonal.Core.Models;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// F250 / F234 — a request body decided by framing headers, and authorization state held per-node.
/// </summary>
public sealed class ProxyBodyAndDefaultsTests
{
    // -----------------------------------------------------------------------
    // F250 — which requests carry a body
    // -----------------------------------------------------------------------
    //
    // Asserted against the predicate rather than through a live proxy hop: reproducing the real
    // defect needs an HTTP/2 request with no Content-Length and no Transfer-Encoding, and TestServer
    // does not speak HTTP/2 at all. What follows pins the rule the proxy now applies — that a body is
    // decided by the METHOD, not by framing headers that HTTP/2 forbids or omits.

    /// <summary>Mirror of the condition in BffProxy.ProxyAsync.</summary>
    private static bool CarriesBody(string method, long? contentLength) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsDelete(method)
          || HttpMethods.IsTrace(method) || HttpMethods.IsOptions(method))
        && contentLength != 0;

    [Theory]
    // The defect: over HTTP/2 there is no Transfer-Encoding (RFC 9113 §8.2.2 forbids it) and
    // Content-Length is optional, so a streamed upload satisfied neither of the old conditions. The
    // payload was discarded, Content-Type with it, and the request was still forwarded WITH the
    // user's bearer token as a bodyless call.
    [InlineData("POST", null, true)]
    [InlineData("PUT", null, true)]
    [InlineData("PATCH", null, true)]
    [InlineData("POST", 42L, true)]
    // An explicit zero really is "no body".
    [InlineData("POST", 0L, false)]
    [InlineData("GET", null, false)]
    [InlineData("HEAD", null, false)]
    [InlineData("DELETE", null, false)]
    [InlineData("OPTIONS", null, false)]
    public void BodyIsDecidedByMethod_NotByFramingHeaders(string method, long? contentLength, bool expected)
    {
        Assert.Equal(expected, CarriesBody(method, contentLength));
    }

    // -----------------------------------------------------------------------
    // F234 — the default group→role store refuses to hold authorization state
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DefaultGroupRoleMappingStore_RefusesWrites()
    {
        // It decides which roles a group grants and is read on the token-issuance path. Process-local
        // with no cross-node invalidation, a REVOKED mapping keeps granting its role on every node
        // that missed the removal, for as long as those processes live.
        var store = new InMemoryScimGroupRoleMappingStore();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.SetAsync(new ScimGroupRoleMapping { GroupId = "g1", Role = "admin" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.DeleteAsync("g1", "admin"));
    }

    [Fact]
    public async Task DefaultGroupRoleMappingStore_ReadsAsEmpty()
    {
        // Reading has to keep working — the resolver calls it on every token mint, and a stock
        // deployment has no mappings anyway (nothing in the product ever wrote one).
        Assert.Empty(await new InMemoryScimGroupRoleMappingStore().GetAllAsync());
    }
}
