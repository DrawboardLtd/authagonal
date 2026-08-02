using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// One protocol surface, whichever host is serving it — so an assertion about <c>/connect/*</c> can be
/// written once and run against both.
/// </summary>
/// <remarks>
/// This exists because of a specific, repeated failure. The tree ships TWO hosts over the same endpoints:
/// <c>Authagonal.Server</c>, and <c>Authagonal.Protocol</c>, which is what goes to nuget.org and what an
/// embedding consumer actually gets. Tests were written against whichever host the author had in front of
/// them, so a fix could land in one and be missed in the other with a green suite either side — the Server
/// host's <c>/connect/userinfo</c> not requiring the <c>openid</c> scope was exactly that, the defect its
/// Protocol twin had already been fixed for, and it is one of eight findings of the same shape.
/// <para>
/// A shared abstraction is the fix rather than discipline: an assertion written through this runs on both by
/// construction, and a host that cannot satisfy the abstraction is a host that cannot be covered, which is
/// itself worth knowing.
/// </para>
/// <para>
/// Deliberately narrow. It carries only what a cross-host protocol assertion needs — a client, the client and
/// scope stores to seed through, and the issuer — because every member added here is a member both hosts have
/// to keep supporting. Anything host-specific belongs in that host's own tests.
/// </para>
/// </remarks>
public interface IProtocolSurfaceHost : IAsyncDisposable
{
    /// <summary>Which host this is, for test output that has to name the failing one.</summary>
    string HostName { get; }

    string Issuer { get; }

    /// <summary>A client that does not follow redirects, so an authorize response can be inspected.</summary>
    HttpClient Client { get; }

    IClientStore Clients { get; }

    IScopeStore Scopes { get; }

    /// <summary>Brings the host up and seeds whatever it needs to answer a token request.</summary>
    Task InitializeAsync();
}

/// <summary>The Server host — <c>AddAuthagonal</c> / <c>UseAuthagonal</c> / <c>MapAuthagonalEndpoints</c>.</summary>
public sealed class ServerSurfaceHost : IProtocolSurfaceHost
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient? _client;

    public string HostName => "Authagonal.Server";
    public string Issuer => AuthagonalTestFactory.TestIssuer;
    public HttpClient Client => _client ?? throw new InvalidOperationException("InitializeAsync first.");
    public IClientStore Clients => _factory.ClientStore;
    public IScopeStore Scopes => _factory.ScopeStore;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>
/// The Protocol host — <c>AddAuthagonalProtocol</c> / <c>MapAuthagonalProtocolEndpoints</c> with no
/// <c>Authagonal.Server</c> at all. This is the shape a consumer of the published package runs.
/// </summary>
public sealed class ProtocolSurfaceHost : IProtocolSurfaceHost
{
    private readonly ProtocolTestHost _host = new();
    private HttpClient? _client;

    public string HostName => "Authagonal.Protocol";
    public string Issuer => ProtocolTestHost.TestIssuer;
    public HttpClient Client => _client ?? throw new InvalidOperationException("InitializeAsync first.");
    public IClientStore Clients => _host.ClientStore;
    public IScopeStore Scopes => _host.ScopeStore;

    public Task InitializeAsync()
    {
        // ProtocolTestHost seeds its own clients and scopes as it starts.
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

/// <summary>
/// The two hosts, as xUnit theory data.
/// </summary>
/// <remarks>
/// Passed as a FACTORY rather than as a live host: xUnit enumerates member data once per class, so returning
/// constructed hosts would share one instance across every theory case and leak a started TestServer between
/// them. Each case builds and disposes its own.
/// </remarks>
public static class BothProtocolHosts
{
    public static TheoryData<Func<IProtocolSurfaceHost>> All() =>
    [
        () => new ServerSurfaceHost(),
        () => new ProtocolSurfaceHost(),
    ];
}
