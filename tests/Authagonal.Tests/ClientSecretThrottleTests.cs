using System.Net;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// An anonymous caller cannot take a confidential client's token issuance offline.
/// </summary>
/// <remarks>
/// Before verifying a client secret, <c>ClientAuthentication</c> called
/// <c>IsRateLimitedAsync($"client-secret|{clientId}", 30, 1 minute)</c>. That call is a
/// check-AND-increment, so every request bumped the counter whether the secret was right or not, and the key
/// carried no source dimension at all.
/// <para>
/// <c>client_id</c> is a public identifier — readable from any SPA's network traffic — and the budget is
/// shared by every endpoint that authenticates through this path: <c>/connect/token</c> (all five grants),
/// <c>/connect/par</c>, <c>/connect/introspect</c>, <c>/connect/revocation</c>,
/// <c>/connect/deviceauthorization</c>. So thirty anonymous requests a minute naming a client took that
/// client's entire token issuance offline, and its own legitimate traffic spent the very budget it was being
/// denied. The comment at the call site claimed the per-client keying meant "one client's traffic cannot
/// lock out another's" — true, and beside the point: it was one SOURCE locking out one client.
/// </para>
/// </remarks>
public sealed class ClientSecretThrottleTests : IAsyncLifetime
{
    private const string ConfidentialClientId = "throttle-client";
    private const string Secret = "s3cret-of-sufficient-length-000000";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // A declared proxy, so the source dimension is the forwarded client address rather than the shared
        // TCP-peer bucket. This is what SourceQuota.Key keys on when the operator has declared their proxy,
        // and it is the only way a test host can present two distinct sources — with nothing declared,
        // TestServer reports no peer at all and every request collapses into one bucket, which is the
        // documented (and separately warned-about) undeclared-proxy behaviour rather than anything this fix
        // changes.
        _factory.Configuration["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1";
        _factory.Configuration["ForwardedHeaders:KnownNetworks:0"] = "0.0.0.0/0";

        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = ConfidentialClientId,
            ClientName = "Throttle Client",
            RequireClientSecret = true,
            ClientSecretHashes = [_factory.Services.GetRequiredService<PasswordHasher>().HashPassword(Secret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid"],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// A flood of wrong secrets from one source does not stop the real client authenticating.
    /// </summary>
    /// <remarks>
    /// The two "sources" here are distinguished the only way a test host can distinguish them: a declared
    /// proxy plus different <c>X-Forwarded-For</c> values, which is what <c>SourceQuota.Key</c> keys on when
    /// the operator has declared their proxy. Without the source dimension both callers share one bucket and
    /// the second request set is refused with <c>invalid_client</c> / "Too many authentication attempts"
    /// having never reached the hash.
    /// </remarks>
    [Fact]
    public async Task AFloodFromOneSourceDoesNotLockOutTheClient()
    {
        // Well past the 30-per-minute budget, all with the wrong secret.
        for (var i = 0; i < 40; i++)
        {
            var attacker = await PostTokenAsync("wrong-secret", from: "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, attacker.StatusCode);
        }

        // The attacker's own budget is spent — that part must still work.
        var spent = await PostTokenAsync(Secret, from: "203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, spent.StatusCode);
        Assert.Contains("Too many", await spent.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The real client, from its own address, is unaffected.
        var legitimate = await PostTokenAsync(Secret, from: "198.51.100.7");
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    /// <summary>The CPU bound still exists: one source cannot make unbounded verification attempts.</summary>
    /// <remarks>
    /// The reason the check sits ahead of the hash. Verification is a ~100k-iteration PBKDF2 on an endpoint
    /// reachable with no credential, so without a bound one request per core saturates the host. Making the
    /// bucket per-source must not turn that bound off.
    /// </remarks>
    [Fact]
    public async Task OneSourceIsStillBounded()
    {
        HttpStatusCode last = HttpStatusCode.OK;
        string body = "";
        for (var i = 0; i < 40; i++)
        {
            var response = await PostTokenAsync("wrong-secret", from: "203.0.113.50");
            last = response.StatusCode;
            body = await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, last);
        Assert.Contains("Too many", body, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> PostTokenAsync(string secret, string from)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ConfidentialClientId,
                ["client_secret"] = secret,
                ["scope"] = "openid",
            }),
        };
        request.Headers.Add("X-Forwarded-For", from);
        return await _client.SendAsync(request);
    }
}
