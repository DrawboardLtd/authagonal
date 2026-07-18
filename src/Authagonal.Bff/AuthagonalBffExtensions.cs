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
    public static IServiceCollection AddAuthagonalBff(this IServiceCollection services, Action<AuthagonalBffOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuthagonalBffOptions>()
            .Configure(configure)
            .PostConfigure(static o => o.Validate());

        services.AddDataProtection();
        services.AddDistributedMemoryCache(); // TryAdd inside: a real IDistributedCache wins if registered
        services.AddHttpClient("AuthagonalBff");
        services.AddHttpClient("AuthagonalBffProxy");

        services.TryAddSingleton<BffOidcConfig>();
        services.TryAddSingleton<ICookieProtector, DataProtectionCookieProtector>();
        services.TryAddSingleton<IBffSessionStore, DistributedCacheBffSessionStore>();
        services.TryAddSingleton<ITokenClient, AuthagonalTokenClient>();
        services.TryAddSingleton<BffRefreshCoordinator>();

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
        // Server-to-server callback from the IdP (signed logout_token authenticates it) — no CSRF header,
        // and antiforgery disabled so a host using UseAntiforgery still admits the form POST.
        group.MapPost("/backchannel-logout", BffEndpoints.BackChannelLogoutAsync).DisableAntiforgery();

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
