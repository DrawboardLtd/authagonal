using System.Net.Http;
using Authagonal.Core.Services;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Authagonal.Protocol;

/// <summary>
/// Entry points for wiring Authagonal.Protocol into an ASP.NET Core host.
/// <para>
/// Hosts are responsible for:
/// <list type="bullet">
///   <item>Registering their own <see cref="IOidcSubjectResolver"/></item>
///   <item>Registering implementations of <c>IClientStore</c>, <c>IGrantStore</c>,
///     <c>IScopeStore</c>, <c>ISigningKeyStore</c>, <c>ITenantContext</c></item>
///   <item>Registering the <see cref="AuthagonalProtocolOptions.AuthenticationScheme"/>
///     scheme (e.g. cookies, custom share-link handler)</item>
/// </list>
/// Calling <see cref="AddAuthagonalProtocol"/> registers the token service, key manager,
/// client/scope seeder, and supporting services.
/// </para>
/// <para>
/// <b>TLS.</b> <c>/connect/authorize</c>, <c>/connect/token</c>, <c>/connect/userinfo</c> and
/// <c>/connect/par</c> refuse plaintext http, per RFC 6749 §3.1/§3.2. That is enforced by a filter on the
/// endpoints themselves rather than by middleware, because this package is mapped into a pipeline it does
/// not own — so it holds no matter how the host composes that pipeline. A host behind a TLS-terminating
/// proxy needs <c>UseForwardedHeaders</c> (which it needs anyway, for Secure cookies and correct absolute
/// URLs) with that proxy declared in <c>KnownProxies</c>/<c>KnownNetworks</c>, since an empty trust set
/// lets any caller set the scheme; a host that genuinely serves this surface over http sets
/// <see cref="AuthagonalProtocolOptions.AllowInsecureHttp"/>.
/// </para>
/// </summary>
public static class AuthagonalProtocolExtensions
{
    public static IServiceCollection AddAuthagonalProtocol(
        this IServiceCollection services,
        Action<AuthagonalProtocolOptions> configure)
    {
        services.Configure(configure);
        return services.AddAuthagonalProtocolCore();
    }

    public static IServiceCollection AddAuthagonalProtocol(
        this IServiceCollection services,
        IConfiguration configurationSection)
    {
        services.Configure<AuthagonalProtocolOptions>(configurationSection);
        return services.AddAuthagonalProtocolCore();
    }

    private static IServiceCollection AddAuthagonalProtocolCore(this IServiceCollection services)
    {
        // Token service + auth-code service — scoped so they pick up per-tenant stores in
        // multi-tenant hosts and singleton stores in embedded hosts via the scope provider.
        services.AddScoped<IProtocolTokenService, ProtocolTokenService>();
        services.AddScoped<ProtocolAuthorizationCodeService>();
        services.AddScoped<ProtocolPushedAuthorizationService>();

        // Key manager — only register if the host hasn't brought its own. Multi-tenant
        // hosts (e.g. Cloud with VaultTransitKeyManager) register their IKeyManager ahead
        // of this call and we must not shadow it with the default singleton pipeline.
        if (!services.Any(d => d.ServiceType == typeof(IKeyManager)))
        {
            services.AddSingleton<ProtocolKeyManager>();
            services.AddSingleton<IKeyManager>(sp => sp.GetRequiredService<ProtocolKeyManager>());
            services.AddHostedService(sp => sp.GetRequiredService<ProtocolKeyManager>());
        }

        // Default client-secret verifier (BCrypt). Hosts with a different hasher TryAdd
        // their own ahead of this call.
        services.TryAddSingleton<IClientSecretVerifier, BCryptClientSecretVerifier>();

        // Default rate limiter, so the throttles in this package are not dead code in a Protocol-only
        // host.
        //
        // Both of them resolved the limiter optionally and simply did not apply when a host registered
        // none, and nothing here registered one — so the bound existed only in hosts that happened to
        // bring one. For an embedder that calls AddAuthagonalProtocol without AddAuthagonal, that left
        // /connect/token, /connect/introspect, /connect/revocation and /connect/par as an unbounded
        // client-secret guessing oracle AND a CPU amplifier (each attempt spends a full KDF on an
        // endpoint reachable with no credential); PAR additionally is an anonymous write that persists a
        // grant row per request and needs only a public client_id to reach.
        //
        // Per-node and untenanted, which is the same backstop the full server ships. TryAdd, so a host
        // with a distributed or tenant-scoped limiter (Authagonal.Server has both) still wins — which is
        // why that host registers its own BEFORE calling this.
        services.TryAddSingleton<Core.Services.IRateLimiter, Core.Services.InProcessRateLimiter>();

        // Token-exchange host seam — no-op unless the host registers its own transformer ahead
        // of this call (context-bound tokens: validate extra params, force binding claims).
        services.TryAddSingleton<ITokenExchangeSubjectTransformer, NullTokenExchangeSubjectTransformer>();

        // Client-credentials host seam — the machine-caller counterpart of the exchange transformer:
        // a first-party service client can name the context it acts in (e.g. organization_id) and the
        // host forces the validated claims onto the token. No-op unless the host registers its own.
        services.TryAddSingleton<IClientCredentialsClaimsTransformer, NullClientCredentialsClaimsTransformer>();

        // Per-user scope entitlement (Scope.AllowedRoles). Registered here rather than only in
        // AddAuthagonal so every host embedding the protocol surface gets it — the authorize
        // endpoint takes it as a service, and an unregistered service on a GET binds as a BODY
        // parameter instead, which fails as an opaque empty 400 rather than a missing-dependency
        // error. Ungated scopes pass through untouched, so this changes nothing until a scope is gated.
        services.TryAddSingleton<Core.Services.IScopeRoleGate, Core.Services.ScopeRoleGate>();

        // The agentic seams (IAgentProfileStore, IConnectorCatalog) are deliberately NOT
        // defaulted here: AddAuthagonal wires protocol services before storage, so a TryAdd
        // fallback would shadow the provider's real store. ProtocolTokenService takes them as
        // optional constructor dependencies instead — absent means "no client is an agent".

        // The client-JWKS fetch (private_key_jwt at /connect/token, ClientAuthentication).
        //
        // ClientAuthentication has always asked the factory for this name, and no host had ever
        // registered it — so the factory handed back the default handler, which follows up to 50
        // redirects. jwks_uri is admin- or DCR-settable and the fetch is reachable from an anonymous
        // token request, so the SSRF guard that runs on the configured URL was walked past by a single
        // `302 Location: https://<internal-host>/`: .NET only refuses https→http, and an https target
        // inside a service mesh is exactly what an internal-network probe wants. Registered in the
        // protocol package rather than in AddAuthagonal because ClientAuthentication lives here and a
        // Protocol-only host must not be the one that misses it.
        services.AddHttpClient("AuthagonalJwks")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // Refuses an internal address at the socket, per connection and therefore per
                // redirect hop, whatever the hostname resolved to. See SafeOutboundConnect.
                ConnectCallback = Authagonal.Core.Services.SafeOutboundConnect.Callback(),
                // Without this the guard above is decoration. With a proxy in effect SocketsHttpHandler
                // invokes ConnectCallback with the PROXY's endpoint and never the target's, so the check
                // passes on the proxy's own perfectly routable address and the request goes wherever
                // jwks_uri said — failing open in the deployments most likely to have a proxy. There is no
                // switch: this target is registrant-chosen and reachable from an anonymous /connect/token.
                UseProxy = false,
            });

        // Seeds clients/scopes from AuthagonalProtocolOptions on startup.
        services.AddHostedService<ProtocolSeedService>();

        return services;
    }

    /// <summary>
    /// Maps the five core OIDC endpoints: discovery, JWKS, authorize, token, userinfo.
    /// </summary>
    /// <summary>
    /// Maps the protocol surface: discovery, JWKS, authorize, token, userinfo and PAR.
    /// </summary>
    /// <remarks>
    /// The four <c>/connect/*</c> endpoints carry an RFC 6749 §3.1/§3.2 TLS filter and will refuse
    /// plaintext http unless <see cref="AuthagonalProtocolOptions.AllowInsecureHttp"/> is set. Mapping an
    /// endpoint individually rather than through this method does not opt out of it — the filter is
    /// attached where each route is declared. Discovery and JWKS are not gated: they are public metadata,
    /// and a client that cannot read them cannot discover it needs https in the first place.
    /// </remarks>
    public static IEndpointRouteBuilder MapAuthagonalProtocolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapProtocolDiscoveryEndpoint();
        app.MapProtocolJwksEndpoint();
        app.MapProtocolAuthorizeEndpoint();
        app.MapProtocolTokenEndpoint();
        app.MapProtocolUserinfoEndpoint();
        app.MapProtocolPushedAuthorizationEndpoint();
        return app;
    }
}
