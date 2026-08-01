using System.Net;
using System.Net.Http.Json;
using Authagonal.Server.Services.Cluster;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Tests;

/// <summary>
/// A per-source quota is only a quota while the caller cannot choose the source.
/// </summary>
/// <remarks>
/// With no proxy declared, <c>UseAuthagonal</c> still honours <c>X-Forwarded-For</c> from the
/// loopback/private ranges — a deliberate guess, documented as never load-bearing for a security
/// decision. The registration and DCR limiters were keyed on <c>Connection.RemoteIpAddress</c>, which is
/// exactly the value that guess produces, so any caller whose immediate peer sits in those ranges (an L4
/// load balancer, a docker bridge, pod-to-pod) got a fresh bucket per request by varying one header —
/// unbounded account creation and unbounded client-record creation from one host.
/// </remarks>
public sealed class ForgedClientAddressTests
{
    // ── The decision itself ──────────────────────────────────────────

    /// <summary>
    /// Undeclared proxy: the rewritten RemoteIpAddress is a client-supplied value, so the quota keys on
    /// the peer that was actually observed.
    /// </summary>
    [Fact]
    public void TrustedClientAddress_withNoDeclaredProxy_usesTheRawPeer()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[InternalEndpointGuard.RawPeerAddressItem] = IPAddress.Parse("10.4.0.9");
        ctx.Items[InternalEndpointGuard.ProxyTrustDeclaredItem] = false;
        // What UseForwardedHeaders left behind after honouring the header.
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal("10.4.0.9", InternalEndpointGuard.TrustedClientAddress(ctx));
    }

    /// <summary>
    /// Declared proxy: the header can only have been set by the proxy the operator named, so it is
    /// evidence — and keying on it is what keeps one client from throttling every other client.
    /// </summary>
    [Fact]
    public void TrustedClientAddress_withADeclaredProxy_usesTheForwardedClient()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[InternalEndpointGuard.RawPeerAddressItem] = IPAddress.Parse("10.4.0.9");
        ctx.Items[InternalEndpointGuard.ProxyTrustDeclaredItem] = true;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal("203.0.113.9", InternalEndpointGuard.TrustedClientAddress(ctx));
    }

    /// <summary>
    /// No capture middleware at all (a host that maps the endpoints without UseAuthagonal) reads as
    /// undeclared, and a forwarded header then makes the peer unknowable — one shared bucket, which
    /// throttles too hard rather than not at all.
    /// </summary>
    [Fact]
    public void TrustedClientAddress_withNoMiddlewareAndAForgedHeader_failsClosed()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal("unknown", InternalEndpointGuard.TrustedClientAddress(ctx));
    }

    // ── End to end, through the real pipeline ────────────────────────

    /// <summary>
    /// The registration cap (5 per hour by default) must bind across requests that vary only the
    /// forwarded header. Before, each distinct value minted a new bucket and the cap never fired.
    /// </summary>
    [Fact]
    public async Task Registration_cap_survives_a_rotating_XForwardedFor()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient();
        await factory.SeedTestDataAsync();

        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < 8; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
            {
                Content = JsonContent.Create(new
                {
                    email = $"flood-{Guid.NewGuid():N}@example.test",
                    password = "Str0ng!Passw0rd-Value",
                }),
            };
            // A different "client" every time, as far as the header is concerned.
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            lastResponse = await client.SendAsync(request);

            if (lastResponse.StatusCode == HttpStatusCode.TooManyRequests)
                return;
        }

        Assert.Fail(
            $"8 registrations from one peer all succeeded (last status {lastResponse!.StatusCode}); " +
            "the per-source cap is keyed on a value the caller supplies");
    }

    /// <summary>
    /// The same, for the other IP-keyed limiter: anonymous dynamic client registration, 10 per hour.
    /// Client-record flooding is the cheaper of the two attacks — no mail, no user records, just rows.
    /// </summary>
    [Fact]
    public async Task Dcr_cap_survives_a_rotating_XForwardedFor()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.DynamicClientRegistrationEnabled = true,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        for (var i = 0; i < 14; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/connect/register")
            {
                Content = JsonContent.Create(new
                {
                    client_name = $"flood-{i}",
                    redirect_uris = new[] { "https://flood.example/callback" },
                }),
            };
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return;
        }

        Assert.Fail("14 anonymous client registrations from one peer all succeeded");
    }
}
