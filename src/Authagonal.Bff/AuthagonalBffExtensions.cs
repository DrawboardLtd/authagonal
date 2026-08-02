using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Authagonal.Bff;

/// <summary>Registration and endpoint-mapping entry points for the Authagonal BFF.</summary>
public static class AuthagonalBffExtensions
{
    /// <summary>Registers the BFF services. Register a distributed cache (e.g. Redis) before this call if
    /// you run more than one instance; otherwise an in-memory cache is used.</summary>
    /// <remarks>
    /// A shared cache is necessary for multi-instance operation but NOT sufficient. Refresh
    /// single-flight (<see cref="BffRefreshCoordinator"/>) is per-process unless you give it a
    /// cross-process lock, so two replicas can read the same session, both find it needs refreshing,
    /// and both redeem the same rotating refresh token. The IdP reads a second redemption as a
    /// stolen-token replay and revokes the whole grant family — signing the user out everywhere, from
    /// an entirely ordinary event.
    /// <para>
    /// Running more than one instance therefore needs ONE of these two, and preferably both:
    /// </para>
    /// <list type="number">
    /// <item>
    /// An <see cref="Core.Clustering.ILeaseProvider"/> registered in this container — the coordinator
    /// then redeems under a per-session lease and the other replicas wait for the result instead of
    /// redeeming. Any backend works; the storage providers ship one (<c>AddAuthagonalClustering</c>
    /// with <c>UseAzureStorage</c> / the AWS / SQL equivalents), and a BFF host that is not the
    /// identity server can register a provider directly.
    /// </item>
    /// <item>
    /// A non-zero <c>Auth:RefreshTokenReuseGraceSeconds</c> on the identity provider (30 is the
    /// protocol layer's own default; the Server host's own default is 0, i.e. strict), which serves
    /// the successor idempotently inside that window instead of revoking. This one also covers
    /// clients outside the BFF that can refresh concurrently.
    /// </item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddAuthagonalBff(this IServiceCollection services, Action<AuthagonalBffOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuthagonalBffOptions>()
            .Configure(configure)
            .PostConfigure(static o => o.Validate());

        services.AddDataProtection();
        services.AddDistributedMemoryCache(); // TryAdd inside: a real IDistributedCache wins if registered
        // Redirects are not followed on either client.
        //
        // BffProxy composes the upstream URL and then refuses it if it left the configured upstream
        // authority — a guard an automatic 302 walks straight past, carrying the session's bearer token
        // to wherever the upstream pointed. Relaying the 3xx to the browser is also the correct proxy
        // behaviour: the response is copied through verbatim, so the caller sees the redirect the
        // upstream actually sent instead of a body fetched from somewhere else. The token client talks
        // to the configured issuer, where a redirect would move the client_secret and the authorization
        // code off it.
        //
        // Neither carries the SSRF address guard (Core's SafeOutboundConnect), and that is the correct
        // answer for both rather than an omission. That guard refuses every private, loopback and
        // link-local address, and it belongs on a client whose target a REGISTRANT chose, where naming an
        // internal host is the attack. Here the operator chose the target and an internal host is the
        // deployment: BffUpstream.TargetBaseUrl is a URL from this host's own configuration whose own
        // documented example is https://api.internal.acme.com, and a BFF sitting in front of an API in the
        // same cluster is the ordinary case rather than the exotic one. The token client is the same story
        // one step removed — it posts to the endpoints published by the authority this host configured, and
        // an identity server reachable on a private address is a supported topology. There is also nothing
        // for an address check to add: BffProxy re-composes the target as a URI and refuses it if it left
        // the configured upstream authority, so the caller cannot steer the request at all.
        services.AddHttpClient("AuthagonalBff")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });
        services.AddHttpClient("AuthagonalBffProxy")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });

        services.TryAddSingleton<BffOidcConfig>();
        services.TryAddSingleton<ICookieProtector, DataProtectionCookieProtector>();
        services.TryAddSingleton<IBffSessionStore, DistributedCacheBffSessionStore>();
        services.TryAddSingleton<ITokenClient, AuthagonalTokenClient>();
        // Single-tenant by default. A host serving many tenants registers its own IBffTenantResolver
        // (AddSingleton) before or after this call — TryAdd keeps the custom one — and sets
        // AuthagonalBffOptions.TenantQueryParam so /bff/login reads the tenant key.
        services.TryAddSingleton<IBffTenantResolver, StaticBffTenantResolver>();
        services.TryAddSingleton<BffRefreshCoordinator>();
        services.TryAddSingleton<BffExchangedTokens>();

        return services;
    }

    /// <summary>Maps the BFF endpoints: <c>{BasePath}/login</c>, <c>{BasePath}/user</c>,
    /// <c>{BasePath}/logout</c>, and the callback at <c>CallbackPath</c>.</summary>
    public static IEndpointRouteBuilder MapAuthagonalBff(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AuthagonalBffOptions>>().Value;

        var group = endpoints.MapGroup(options.BasePath);
        group.MapGet("/login", BffEndpoints.LoginAsync);
        group.MapGet("/user", BffEndpoints.UserAsync);
        group.MapMethods("/logout", ["GET", "POST"], BffEndpoints.LogoutAsync);
        // Post-logout landing for the RP-initiated end_session round trip when /logout was given a returnUrl.
        // Must be registered as a post_logout_redirect_uri for the BFF's OIDC client.
        group.MapGet("/logout-callback", BffEndpoints.LogoutCallback);
        // Server-to-server callback from the IdP (signed logout_token authenticates it) — no CSRF header,
        // and antiforgery disabled so a host using UseAntiforgery still admits the form POST.
        group.MapPost("/backchannel-logout", BffEndpoints.BackChannelLogoutAsync).DisableAntiforgery();

        // Websocket ticket minting (opt-in): a scripted GET guarded by the anti-forgery header. Needs a
        // SHARED distributed cache (Redis) — see AuthagonalBffOptions.WsTicketsEnabled.
        if (options.WsTicketsEnabled)
            group.MapGet("/ws-ticket", BffEndpoints.WsTicketAsync);

        // Token-injecting proxy (only if upstreams are configured). It does its own anti-forgery-header
        // check and forwards arbitrary content, so the framework antiforgery filter is disabled here.
        if (options.Upstreams.Count > 0)
            group.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"], BffProxy.ProxyAsync)
                .DisableAntiforgery();

        // The callback lives at its own configurable absolute path (must match the registered redirect URI),
        // which may sit outside the base path.
        endpoints.MapGet(options.CallbackPath, BffEndpoints.CallbackAsync);

        return endpoints;
    }
}
