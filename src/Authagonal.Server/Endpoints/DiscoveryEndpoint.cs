using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;

namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", async (
            Authagonal.Core.Services.ITenantContext tenantContext,
            Authagonal.Core.Stores.IScopeStore scopeStore,
            Microsoft.Extensions.Options.IOptions<Authagonal.Server.Services.AuthOptions> authOptions,
            Microsoft.AspNetCore.Http.HttpResponse response,
            CancellationToken ct) =>
        {
            // Edge/CDN-cacheable: per-tenant discovery metadata changes rarely.
            response.Headers.CacheControl = "public, max-age=3600";
            var issuer = tenantContext.Issuer;

            var scopesSupported = await DiscoveryHelpers.ResolveSupportedScopesAsync(scopeStore, ct);

            return TypedResults.Json(new DiscoveryResponse
            {
                Issuer = issuer,
                AuthorizationEndpoint = $"{issuer}/connect/authorize",
                TokenEndpoint = $"{issuer}/connect/token",
                UserinfoEndpoint = $"{issuer}/connect/userinfo",
                JwksUri = $"{issuer}/.well-known/openid-configuration/jwks",
                RevocationEndpoint = $"{issuer}/connect/revocation",
                IntrospectionEndpoint = $"{issuer}/connect/introspect",
                EndSessionEndpoint = $"{issuer}/connect/endsession",
                DeviceAuthorizationEndpoint = $"{issuer}/connect/deviceauthorization",
                RegistrationEndpoint = authOptions.Value.DynamicClientRegistrationEnabled ? $"{issuer}/connect/register" : null,
                PushedAuthorizationRequestEndpoint = $"{issuer}/connect/par",
                ScopesSupported = scopesSupported,
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code"],
                SubjectTypesSupported = ["public"],
                IdTokenSigningAlgValuesSupported = ["ES256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post"],
                CodeChallengeMethodsSupported = ["S256"],
                BackchannelLogoutSupported = true,
                BackchannelLogoutSessionSupported = false,
                FrontchannelLogoutSupported = true,
                FrontchannelLogoutSessionSupported = true,
                ClaimsSupported = ["sub", "iss", "aud", "exp", "iat", "auth_time", "email", "email_verified", "name", "given_name", "family_name", "phone_number", "roles", "groups"],
            }, ProtocolJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
