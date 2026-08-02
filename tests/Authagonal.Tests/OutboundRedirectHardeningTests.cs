using System.Net;
using Authagonal.Bff;
using Authagonal.Core.Services;
using Authagonal.Protocol;
using Authagonal.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// Every server-initiated HttpClient in this product must refuse to follow redirects.
/// </summary>
/// <remarks>
/// The SSRF guard runs on the URL the caller supplied, once. The framework default is to follow up to
/// 50 redirects automatically, so a single <c>302</c> from the (admin-settable, DCR-settable, or merely
/// compromised) remote host sends the request somewhere nothing checked — and .NET only refuses
/// https→http, so an https target inside a service mesh stays reachable. Where a redirect legitimately
/// has to be followed, <c>SafeOutboundHttp</c> resolves the hops itself and re-runs the guard on each.
/// <para>
/// Three clients were hardened when this was first reported and the rest were left. Two of them —
/// "AuthagonalJwks" and "BackChannelLogout" — were named at their call sites and registered by nobody,
/// which is invisible: <c>CreateClient</c> on an unregistered name silently returns a default-configured
/// client. This test enumerates every name the product asks the factory for, so a new one cannot be
/// added without either hardening it or failing here.
/// </para>
/// </remarks>
public sealed class OutboundRedirectHardeningTests
{
    /// <summary>
    /// Clients whose target comes from OPERATOR configuration or an admin API. The address guard applies and
    /// <c>Auth:AllowedInternalTargets</c> can widen it, because for these an internal host is frequently the
    /// deployment: an on-premises IdP, a provisioning app in the same cluster.
    /// </summary>
    private static readonly string[] OperatorConfiguredClients = ["Provisioning", "SamlMetadata", "OidcDiscovery"];

    /// <summary>
    /// Clients whose target is chosen by a REGISTRANT or a client — a <c>jwks_uri</c>, a back-channel logout
    /// URI. The address guard applies with no way to widen it and no proxy, because here an internal host is
    /// never the deployment.
    /// </summary>
    private static readonly string[] RegistrantSuppliedClients = ["BackChannelLogout", "AuthagonalJwks"];

    /// <summary>
    /// Clients whose target is a compile-time constant. Nothing chooses it, so there is nothing for an
    /// address check to decide.
    /// </summary>
    private static readonly string[] FixedTargetClients = ["Resend"];

    /// <summary>
    /// Every named client the product creates. Keep in step with CreateClient call sites — and note that it
    /// is DERIVED from the three groups above, so a new client cannot be added to this list without
    /// someone deciding who chooses its target, which is the decision that was got wrong once already.
    /// </summary>
    public static TheoryData<string> ServerClientNames() =>
        [.. OperatorConfiguredClients, .. RegistrantSuppliedClients, .. FixedTargetClients];

    public static TheoryData<string> OperatorConfiguredClientNames() => [.. OperatorConfiguredClients];

    public static TheoryData<string> RegistrantSuppliedClientNames() => [.. RegistrantSuppliedClients];

    public static TheoryData<string> BffClientNames() => ["AuthagonalBff", "AuthagonalBffProxy"];

    private static ServiceProvider ServerServices(
        IEnumerable<string>? allowedInternalTargets = null, bool allowOutboundProxy = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Issuer"] = "https://auth.test",
            ["Email:ResendApiKey"] = "re_test",
            ["Auth:AllowOutboundProxy"] = allowOutboundProxy ? "true" : "false",
        };

        var index = 0;
        foreach (var target in allowedInternalTargets ?? [])
            settings[$"Auth:AllowedInternalTargets:{index++}"] = target;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthagonalCore(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BffServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthagonalBff(o =>
        {
            o.Authority = "https://auth.test";
            o.ClientId = "bff";
            o.ClientSecret = "secret";
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Walks the handler pipeline to the primary handler. A named client with no explicit primary
    /// handler gets a default <c>HttpClientHandler</c>, whose AllowAutoRedirect is true — which is the
    /// state this test exists to catch.
    /// </summary>
    private static void AssertDoesNotFollowRedirects(IServiceProvider provider, string name)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var allowsRedirects = handler switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler http => http.AllowAutoRedirect,
            _ => true,
        };

        Assert.False(allowsRedirects,
            $"the '{name}' client follows redirects: a 302 from the remote host walks past the SSRF guard, " +
            "which only ever saw the URL the caller supplied");
    }

    [Theory]
    [MemberData(nameof(ServerClientNames))]
    public void ServerNamedClients_DoNotFollowRedirects(string name)
    {
        using var provider = ServerServices();
        AssertDoesNotFollowRedirects(provider, name);
    }

    /// <summary>
    /// The jwks_uri fetch lives in Authagonal.Protocol, so a host built on the protocol package alone —
    /// without AddAuthagonal — must get the hardened client too. It is registered there for that reason.
    /// </summary>
    [Fact]
    public void ProtocolOnlyHost_GetsTheHardenedJwksClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthagonalProtocol(_ => { });
        using var provider = services.BuildServiceProvider();

        AssertDoesNotFollowRedirects(provider, "AuthagonalJwks");
    }

    [Theory]
    [MemberData(nameof(BffClientNames))]
    public void BffNamedClients_DoNotFollowRedirects(string name)
    {
        using var provider = BffServices();
        AssertDoesNotFollowRedirects(provider, name);
    }

    /// <summary>
    /// Every outbound client also carries a bounded timeout. The framework default is 100 seconds, which
    /// on an anonymous endpoint makes a deliberately slow remote host a request-slot amplifier.
    /// </summary>
    [Theory]
    [MemberData(nameof(ServerClientNames))]
    public void ServerNamedClients_HaveABoundedTimeout(string name)
    {
        using var provider = ServerServices();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        Assert.True(client.Timeout <= TimeSpan.FromSeconds(30),
            $"the '{name}' client is on the 100-second default timeout");
    }

    // ── Which clients carry the socket-level address guard, and what a proxy does to it ──────────────
    //
    // SafeOutboundConnect was once attached to all ten SocketsHttpHandler registrations in one scripted
    // pass. Several of those clients exist SPECIFICALLY to reach internal addresses — the BFF's
    // token-injecting reverse proxy documents https://api.internal.acme.com as its own example — so the
    // guard became the outage. And none of the ten set UseProxy = false, which means that wherever a proxy
    // was in effect the callback was handed the PROXY's endpoint and the guard passed on it every time:
    // the deployments most likely to have a proxy were the ones where it failed open. These tests pin both
    // halves, per client, because the decision is per client and the last attempt to make it uniformly was
    // wrong in both directions at once.

    private static SocketsHttpHandler PrimarySocketsHandler(IServiceProvider provider, string name)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    [Theory]
    [MemberData(nameof(OperatorConfiguredClientNames))]
    [MemberData(nameof(RegistrantSuppliedClientNames))]
    public void GuardedClients_ResolveAndPinTheAddressAtTheSocket(string name)
    {
        using var provider = ServerServices();

        Assert.NotNull(PrimarySocketsHandler(provider, name).ConnectCallback);
    }

    /// <summary>
    /// A guarded client does not use the ambient proxy by default, in either group.
    /// </summary>
    /// <remarks>
    /// This is the difference between a guard and a decoration. <c>SocketsHttpHandler</c> invokes
    /// <c>ConnectCallback</c> with the endpoint it is about to open a socket to, and with a proxy in effect
    /// that is the PROXY — so the callback resolves the proxy's name, finds a perfectly routable address,
    /// permits it, and the request then travels to whatever the target URL said. The guard reports success
    /// while inspecting the wrong host.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OperatorConfiguredClientNames))]
    [MemberData(nameof(RegistrantSuppliedClientNames))]
    public void GuardedClients_DoNotUseTheAmbientProxyByDefault(string name)
    {
        using var provider = ServerServices();

        Assert.False(PrimarySocketsHandler(provider, name).UseProxy,
            $"the '{name}' client would send its request through a proxy, and its ConnectCallback would " +
            "then only ever see the proxy's address — the guard passes and the request goes anywhere");
    }

    /// <summary>
    /// <c>Auth:AllowOutboundProxy</c> reaches the operator-configured clients and stops there.
    /// </summary>
    /// <remarks>
    /// A deployment whose egress requires a proxy has to be able to fetch its own upstream IdP's metadata,
    /// and the operator configured that URL — trusting their proxy decision is consistent with trusting
    /// their URL. The registrant-supplied clients are the opposite case: the target is chosen by whoever
    /// registered the client, the fetch is reachable from an anonymous request, and the address check is the
    /// only thing between the two. There is deliberately no configuration that turns that off, because a
    /// switch which did would be the first thing flipped while debugging something unrelated.
    /// </remarks>
    [Fact]
    public void AllowOutboundProxy_ReachesTheOperatorConfiguredClientsOnly()
    {
        using var provider = ServerServices(allowOutboundProxy: true);

        foreach (var name in OperatorConfiguredClients)
            Assert.True(PrimarySocketsHandler(provider, name).UseProxy,
                $"'{name}' ignored Auth:AllowOutboundProxy, so a proxy-only egress cannot reach its target");

        foreach (var name in RegistrantSuppliedClients)
            Assert.False(PrimarySocketsHandler(provider, name).UseProxy,
                $"Auth:AllowOutboundProxy reached '{name}', whose target a registrant chooses — the switch " +
                "has just turned off the address guard on an anonymously reachable fetch");
    }

    /// <summary>
    /// The operator's allowlist is built from configuration and available to the guard.
    /// </summary>
    /// <remarks>
    /// The consumers take it as an OPTIONAL dependency, so a missing registration is silent — and silently
    /// STRICT, which is the safe direction but leaves an operator with a configuration key that does
    /// nothing. Asserted here because "it fails closed" is not the same as "it works".
    /// </remarks>
    [Fact]
    public void TheOperatorAllowlist_IsBuiltFromConfiguration()
    {
        using var provider = ServerServices(allowedInternalTargets: ["idp.corp.internal", "10.4.0.0/16"]);
        var allowlist = provider.GetRequiredService<OutboundAllowlist>();

        Assert.True(allowlist.PermitsHost("idp.corp.internal"));
        Assert.True(allowlist.PermitsAddress(IPAddress.Parse("10.4.1.7")));
        Assert.False(allowlist.PermitsHost("idp.attacker.test"));
        Assert.False(allowlist.PermitsAddress(IPAddress.Parse("169.254.169.254")));
    }

    [Fact]
    public void TheOperatorAllowlist_PermitsNothingByDefault()
    {
        using var provider = ServerServices();

        Assert.True(provider.GetRequiredService<OutboundAllowlist>().IsEmpty,
            "the default posture is every internal address refused; the allowlist only ever widens it");
    }

    /// <summary>
    /// Every fetcher on an operator-configured path takes the allowlist, so the URL check and the socket
    /// check reach the same verdict.
    /// </summary>
    /// <remarks>
    /// Both layers have to agree or the deployment fails in whichever one was forgotten, with an error that
    /// names the wrong cause: permit an on-premises IdP at the socket and the URL check still refuses it as
    /// "not permitted", and no amount of correct network configuration helps.
    /// </remarks>
    [Theory]
    [InlineData(typeof(Authagonal.Server.Services.Saml.SamlMetadataParser))]
    [InlineData(typeof(Authagonal.Server.Services.Oidc.OidcDiscoveryClient))]
    [InlineData(typeof(Authagonal.Server.Services.TccProvisioningOrchestrator))]
    public void EveryOperatorConfiguredFetcher_TakesTheAllowlist(Type fetcher)
    {
        var takesIt = fetcher.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(OutboundAllowlist));

        Assert.True(takesIt,
            $"{fetcher.Name} fetches an operator-configured URL but never sees Auth:AllowedInternalTargets, " +
            "so its URL check refuses what its socket check was configured to permit");
    }

    /// <summary>
    /// The BFF's two clients carry no address guard, and that is the decision rather than an omission.
    /// </summary>
    /// <remarks>
    /// <c>BffUpstream.TargetBaseUrl</c> is operator configuration whose own documented example is
    /// <c>https://api.internal.acme.com</c>, and a BFF in front of an API in the same cluster is the
    /// ordinary case. The token client posts to the endpoints published by the authority this host
    /// configured, which may equally be private. There is also nothing an address check could add:
    /// <c>BffProxy</c> re-composes the target as a URI and refuses it if it left the configured upstream
    /// authority, so a caller cannot steer the request at all. Attaching the guard here refused every
    /// private-address upstream at the socket, with no configuration to permit it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BffClientNames))]
    public void BffClients_DoNotCarryTheAddressGuard(string name)
    {
        using var provider = BffServices();

        Assert.Null(PrimarySocketsHandler(provider, name).ConnectCallback);
    }

    /// <summary>
    /// Resend carries no address guard either: its target is a <c>private const</c> (api.resend.com).
    /// </summary>
    /// <remarks>
    /// No configuration and no registrant reaches it, so the guard has nothing to decide — and it would need
    /// <c>UseProxy = false</c> to mean anything, which is how a deployment that egresses through a mandatory
    /// proxy loses all of its email. The redirect refusal is the property that matters on this client and it
    /// is asserted above: the request carries the mail API key in an Authorization header, and .NET only
    /// strips that when a redirect crosses origins.
    /// </remarks>
    [Theory]
    [MemberData(nameof(FixedTargetClientNames))]
    public void FixedTargetClients_DoNotCarryTheAddressGuard(string name)
    {
        using var provider = ServerServices();

        Assert.Null(PrimarySocketsHandler(provider, name).ConnectCallback);
    }

    public static TheoryData<string> FixedTargetClientNames() => [.. FixedTargetClients];
}
