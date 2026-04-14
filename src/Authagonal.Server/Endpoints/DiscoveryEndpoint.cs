namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (Authagonal.Core.Services.ITenantContext tenantContext) =>
        {
            var issuer = tenantContext.Issuer;

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
                ScopesSupported = ["openid", "profile", "email", "offline_access"],
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code"],
                SubjectTypesSupported = ["public"],
                IdTokenSigningAlgValuesSupported = ["RS256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post"],
                CodeChallengeMethodsSupported = ["S256"],
                BackchannelLogoutSupported = true,
                BackchannelLogoutSessionSupported = false,
            }, AuthagonalJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
