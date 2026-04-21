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

        // Seeds clients/scopes from AuthagonalProtocolOptions on startup.
        services.AddHostedService<ProtocolSeedService>();

        return services;
    }

    /// <summary>
    /// Maps the five core OIDC endpoints: discovery, JWKS, authorize, token, userinfo.
    /// </summary>
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
