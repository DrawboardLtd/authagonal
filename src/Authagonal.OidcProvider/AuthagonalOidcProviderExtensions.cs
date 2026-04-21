using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace Authagonal.OidcProvider;

public static class AuthagonalOidcProviderExtensions
{
    /// <summary>
    /// Registers OpenIddict server with authorization_code + PKCE only, using the
    /// options you provide. Consumers must also register <see cref="IOidcSubjectResolver"/>
    /// and configure OpenIddict storage (e.g. <c>AddEntityFrameworkCoreStores</c>) on the
    /// returned builder chain.
    /// </summary>
    public static OpenIddictBuilder AddAuthagonalOidcProvider(
        this IServiceCollection services,
        Action<AuthagonalOidcProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuthagonalOidcProviderOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "Issuer is required.");

        services.TryAddSingleton<IPostConfigureOptions<OpenIddictServerOptions>, ConfigureServerFromOptions>();
        services.AddHostedService<OidcClientSeeder>();

        var builder = services.AddOpenIddict();

        builder.AddServer(server =>
        {
            server.AllowAuthorizationCodeFlow()
                  .AllowRefreshTokenFlow()
                  .RequireProofKeyForCodeExchange();

            server.RegisterScopes(
                OpenIddictConstants.Scopes.OpenId,
                OpenIddictConstants.Scopes.Profile,
                OpenIddictConstants.Scopes.Email,
                OpenIddictConstants.Scopes.OfflineAccess);

            server.UseAspNetCore()
                  .EnableAuthorizationEndpointPassthrough()
                  .EnableTokenEndpointPassthrough()
                  .EnableUserInfoEndpointPassthrough();
        });

        return builder;
    }

    /// <summary>
    /// Maps the default authorize, token, and userinfo endpoint handlers. These handlers
    /// invoke <see cref="IOidcSubjectResolver"/> and emit OpenIddict SignIn results.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthagonalOidcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var opts = endpoints.ServiceProvider.GetRequiredService<IOptions<AuthagonalOidcProviderOptions>>().Value;

        endpoints.MapMethods(
            opts.AuthorizationEndpointPath,
            ["GET", "POST"],
            OidcEndpointHandlers.HandleAuthorizeAsync);

        endpoints.MapPost(
            opts.TokenEndpointPath,
            OidcEndpointHandlers.HandleTokenAsync);

        endpoints.MapMethods(
            opts.UserinfoEndpointPath,
            ["GET", "POST"],
            OidcEndpointHandlers.HandleUserinfoAsync);

        return endpoints;
    }

    private sealed class ConfigureServerFromOptions(IOptions<AuthagonalOidcProviderOptions> options)
        : IPostConfigureOptions<OpenIddictServerOptions>
    {
        public void PostConfigure(string? name, OpenIddictServerOptions server)
        {
            var o = options.Value;
            if (string.IsNullOrWhiteSpace(o.Issuer))
            {
                return;
            }

            server.Issuer = new Uri(o.Issuer, UriKind.Absolute);

            server.AuthorizationEndpointUris.Clear();
            server.AuthorizationEndpointUris.Add(new Uri(o.AuthorizationEndpointPath, UriKind.Relative));

            server.TokenEndpointUris.Clear();
            server.TokenEndpointUris.Add(new Uri(o.TokenEndpointPath, UriKind.Relative));

            server.UserInfoEndpointUris.Clear();
            server.UserInfoEndpointUris.Add(new Uri(o.UserinfoEndpointPath, UriKind.Relative));

            server.JsonWebKeySetEndpointUris.Clear();
            server.JsonWebKeySetEndpointUris.Add(new Uri(o.JwksEndpointPath, UriKind.Relative));

            server.AccessTokenLifetime = o.AccessTokenLifetime;
            server.IdentityTokenLifetime = o.IdentityTokenLifetime;
            server.AuthorizationCodeLifetime = o.AuthorizationCodeLifetime;
            server.RefreshTokenLifetime = o.RefreshTokenLifetime;
        }
    }
}
