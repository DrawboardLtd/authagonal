using Authagonal.Bff;
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
    /// <summary>Every named client the product creates. Keep in step with CreateClient call sites.</summary>
    public static TheoryData<string> ServerClientNames() =>
        ["Provisioning", "SamlMetadata", "OidcDiscovery", "BackChannelLogout", "Resend", "AuthagonalJwks"];

    public static TheoryData<string> BffClientNames() => ["AuthagonalBff", "AuthagonalBffProxy"];

    private static ServiceProvider ServerServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Issuer"] = "https://auth.test",
                ["Email:ResendApiKey"] = "re_test",
            })
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
}
