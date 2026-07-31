using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// RFC 6749 §3.1/§3.2 require TLS at the authorization and token endpoints. UseAuthagonal refuses
/// plaintext requests to /connect/* unless Auth:AllowInsecureHttp says otherwise; TestServer speaks
/// http, so the harness sets that opt-in and these tests turn it back off to exercise the gate.
/// </summary>
public sealed class TransportSecurityTests
{
    [Fact]
    public async Task Authorize_OverPlainHttp_IsRefused_WhenInsecureHttpNotAllowed()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.GetAsync(
            "/connect/authorize?client_id=test-client&response_type=code&scope=openid" +
            "&redirect_uri=https%3A%2F%2Fapp.test%2Fcallback&code_challenge=abc&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("TLS is required", body.RootElement.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Token_OverPlainHttp_IsRefused_WhenInsecureHttpNotAllowed()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.SendAsync(BuildClientCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The gate reads the scheme after forwarded-header processing, which is the whole point: where TLS
    /// terminates at a proxy the request reaches Kestrel as plain http, so X-Forwarded-Proto is the only
    /// truthful answer to "was this encrypted" — but only once the operator has declared which proxy is
    /// entitled to answer it, which is what the KnownNetworks entry here does.
    /// <para>
    /// The peer address is set explicitly to one inside that declared range. An earlier version of
    /// this test left it unset, and TestServer leaves RemoteIpAddress null — which ForwardedHeadersMiddleware
    /// treats as "a server that cannot report a peer" and honours the header unconditionally. It passed
    /// without ever exercising the trust check it was supposed to be demonstrating.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Token_OverPlainHttp_IsAllowed_WhenDeclaredProxyForwardsHttpsScheme()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
            Configuration = { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" },
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/connect/token";
            c.Connection.RemoteIpAddress = TrustedProxy;
            c.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            c.Request.Headers.Authorization = BasicAdminCredentials;
            c.Request.ContentType = "application/x-www-form-urlencoded";
            c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                $"grant_type=client_credentials&scope={AuthagonalTestFactory.AdminScope}"));
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("access_token").GetString()));
    }

    // -----------------------------------------------------------------------
    // The security property: X-Forwarded-Proto is a claim, and a claim is only worth the peer that made
    // it — so the gate may only be satisfied second-hand by a proxy the OPERATOR DECLARED. A private
    // address is not a declaration: this library cannot see the network it was deployed onto, and on a
    // flat one every neighbour holds a private address. So the fallback trust set adjusts the client IP
    // and nothing else. These drive TestServer's context directly because that is the only way to
    // control the peer address, which is the whole variable under test.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("with a matching X-Forwarded-For")]
    [InlineData(null)]
    public async Task Gate_RefusesForgedHttpsClaim_FromUntrustedPeer(string? withForwardedFor)
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            // A public address: not loopback, not RFC1918, so not a proxy this server trusts.
            c.Connection.RemoteIpAddress = UntrustedPeer;
            if (withForwardedFor is not null)
                c.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("TLS is required", await ReadBodyAsync(context));
        // And the forged claim did not take effect anywhere else either.
        Assert.Equal("http", context.Request.Scheme);
    }

    /// <summary>
    /// The control for the case above: same request, same headers, only the peer and the declaration
    /// differ. Without this the refusal above would be consistent with the gate simply refusing
    /// everything.
    /// </summary>
    [Fact]
    public async Task Gate_AcceptsHttpsClaim_FromDeclaredProxy()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
            Configuration = { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" },
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            c.Connection.RemoteIpAddress = TrustedProxy;
            c.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        // Past the gate and into the endpoint, which rejects the missing bearer token on its own terms.
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("https", context.Request.Scheme);
        Assert.DoesNotContain("TLS is required", await ReadBodyAsync(context));
    }

    /// <summary>
    /// The rule that replaced "the peer looks private, so believe its scheme". A private peer is exactly
    /// the case the old fallback covered, and the case that cannot be distinguished from a hostile
    /// neighbour on a flat network — so with nothing declared, the https claim buys nothing.
    /// </summary>
    [Fact]
    public async Task Gate_RefusesHttpsClaim_FromPrivatePeer_WhenNoProxyTrustIsDeclared()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            c.Connection.RemoteIpAddress = TrustedProxy;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("http", context.Request.Scheme);
        // And the refusal names the actual cause, rather than sending the operator hunting for TLS they
        // have already terminated.
        var body = await ReadBodyAsync(context);
        Assert.Contains("no proxy is declared as trusted", body);
        Assert.Contains("ForwardedHeaders:KnownNetworks", body);
    }

    /// <summary>
    /// Declaring a proxy is not declaring every proxy: the peer still has to be the one named. Without
    /// this, a deployment that declared its ingress would be indistinguishable from one that opted out
    /// of the check entirely.
    /// </summary>
    [Fact]
    public async Task Gate_RefusesHttpsClaim_FromPeerOutsideTheDeclaredNetwork()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
            Configuration = { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" },
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            c.Connection.RemoteIpAddress = UntrustedPeer;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("http", context.Request.Scheme);
    }

    /// <summary>
    /// The exit for a deployment whose proxy has no fixed address — a Kubernetes ingress, a rotating
    /// load balancer, a platform that will not tell you the hop's CIDR. "0.0.0.0/0" asserts the thing
    /// that is actually true of such a deployment, that nothing but the proxy can reach this process,
    /// and it is a statement in the operator's config rather than an inference in the library's code.
    /// </summary>
    [Fact]
    public async Task Gate_AcceptsHttpsClaim_FromAnyPeer_WhenTheOperatorDeclaresEveryPeerAProxy()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
            Configuration =
            {
                ["ForwardedHeaders:KnownNetworks:0"] = "0.0.0.0/0",
                ["ForwardedHeaders:KnownNetworks:1"] = "::/0",
            },
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            c.Connection.RemoteIpAddress = UntrustedPeer;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("https", context.Request.Scheme);
    }

    /// <summary>
    /// The other half of the split, and the reason this is not simply "forwarded headers off by
    /// default": with nothing declared, X-Forwarded-For from a private peer STILL sets the client IP,
    /// because a best-effort client IP beats the framework's empty-trust-set behaviour of honouring the
    /// header from every caller alive. Only the scheme requires a declaration.
    /// </summary>
    [Fact]
    public async Task ClientIp_IsStillTakenFromAPrivatePeer_WhenNoProxyTrustIsDeclared()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/health";
            c.Connection.RemoteIpAddress = TrustedProxy;
            c.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(IPAddress.Parse("198.51.100.7"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task Gate_RefusesPlaintext_FromUntrustedPeerWithNoHeaders()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        await factory.SeedTestDataAsync();

        var context = await SendAsync(factory, c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/connect/userinfo";
            c.Connection.RemoteIpAddress = UntrustedPeer;
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("TLS is required", await ReadBodyAsync(context));
    }

    /// <summary>
    /// Only the protocol surface the RFCs name is gated. The health endpoint has to answer over
    /// plaintext or an ingress cannot probe the pod before TLS is in front of it.
    /// </summary>
    [Fact]
    public async Task Health_OverPlainHttp_IsNotGated()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Token_OverPlainHttp_IsAllowed_WhenInsecureHttpOptInIsSet()
    {
        // The harness default — this is what keeps docker-compose and the suite working.
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.SendAsync(BuildClientCredentialsRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static readonly IPAddress UntrustedPeer = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.5");

    private static string BasicAdminCredentials =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}"));

    /// <summary>
    /// Drives the real UseAuthagonal pipeline with control over the connection, which HttpClient does
    /// not give: the peer address is the variable these tests exist to vary.
    /// </summary>
    private static Task<HttpContext> SendAsync(AuthagonalTestFactory factory, Action<HttpContext> configure)
        => ((TestServer)factory.Services.GetRequiredService<IServer>()).SendAsync(c =>
        {
            c.Request.Scheme = "http";
            configure(c);
        });

    private static Task<string> ReadBodyAsync(HttpContext context)
        => new StreamReader(context.Response.Body).ReadToEndAsync();

    private static HttpRequestMessage BuildClientCredentialsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = AuthagonalTestFactory.AdminScope,
            }),
        };
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        return request;
    }
}

/// <summary>
/// The same requirement as seen by a host that embeds Authagonal.Protocol directly — the shape
/// ProtocolTestHost models, and the one shipped on nuget.org. That host composes its own pipeline, so
/// there is no UseAuthagonal middleware to enforce anything; the filter attached to the endpoints is
/// what makes the requirement hold anyway.
/// </summary>
public sealed class ProtocolEmbedderTransportSecurityTests
{
    [Fact]
    public async Task ProtocolToken_OverPlainHttp_IsRefused_WhenEmbedderHasNotOptedIn()
    {
        await using var host = new ProtocolTestHost { AllowInsecureHttp = false };
        var client = host.CreateClient();

        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ProtocolTestHost.MachineClientId,
                ["client_secret"] = ProtocolTestHost.MachineClientSecret,
                ["scope"] = "machine-api",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("TLS is required", body.RootElement.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task ProtocolAuthorize_OverPlainHttp_IsRefused_WhenEmbedderHasNotOptedIn()
    {
        await using var host = new ProtocolTestHost { AllowInsecureHttp = false };
        var client = host.CreateClient();

        var response = await client.GetAsync(
            $"/connect/authorize?client_id={ProtocolTestHost.SpaClientId}&response_type=code&scope=openid" +
            "&redirect_uri=https%3A%2F%2Frp.test%2Fcallback&code_challenge=abc&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TLS is required", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The refusal an embedder actually meets on upgrade: TLS terminated at a proxy, X-Forwarded-Proto
    /// sent, and nothing in the host's pipeline applying it. "Use https" is unactionable advice on a
    /// deployment that already is on https, so the filter names the unapplied header instead.
    /// </summary>
    [Fact]
    public async Task ProtocolToken_RefusalNamesTheUnappliedForwardedProto()
    {
        await using var host = new ProtocolTestHost { AllowInsecureHttp = false };
        var client = host.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ProtocolTestHost.MachineClientId,
                ["client_secret"] = ProtocolTestHost.MachineClientSecret,
                ["scope"] = "machine-api",
            }),
        };
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("X-Forwarded-Proto: https", body);
        Assert.Contains("UseForwardedHeaders", body);
    }

    /// <summary>
    /// Discovery is deliberately outside the gate: it is public metadata, and a client that cannot read
    /// it cannot learn that it needs https in the first place.
    /// </summary>
    [Fact]
    public async Task ProtocolDiscovery_OverPlainHttp_IsNotGated()
    {
        await using var host = new ProtocolTestHost { AllowInsecureHttp = false };
        var client = host.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtocolToken_OverPlainHttp_IsAllowed_WhenEmbedderOptsIn()
    {
        await using var host = new ProtocolTestHost { AllowInsecureHttp = true };
        var client = host.CreateClient();

        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ProtocolTestHost.MachineClientId,
                ["client_secret"] = ProtocolTestHost.MachineClientSecret,
                ["scope"] = "machine-api",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// SecretProvider:RequireVaultReferences closes the unprefixed-reference bypass in the vault-backed
/// providers, where a reference without a <c>kv:</c> / <c>sm:</c> prefix is otherwise returned as the
/// secret value itself.
/// </summary>
public sealed class VaultReferenceRequirementTests
{
    [Fact]
    public async Task KeyVault_UnprefixedReference_IsReturnedVerbatim_ByDefault()
    {
        // The vault client is never reached on this path — that is exactly the problem being pinned.
        var provider = new KeyVaultSecretProvider(
            null!, new SecretProviderOptions(), NullLogger<KeyVaultSecretProvider>.Instance);

        Assert.Equal("legacy-plaintext-secret", await provider.ResolveAsync("legacy-plaintext-secret"));
    }

    [Fact]
    public async Task KeyVault_UnprefixedReference_Throws_WhenVaultReferencesRequired()
    {
        var provider = new KeyVaultSecretProvider(
            null!,
            new SecretProviderOptions { RequireVaultReferences = true },
            NullLogger<KeyVaultSecretProvider>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ResolveAsync("legacy-plaintext-secret"));

        // The message must never quote the reference: on this branch it IS the cleartext secret.
        Assert.DoesNotContain("legacy-plaintext-secret", ex.Message);
        Assert.Contains("RequireVaultReferences", ex.Message);
    }
}
