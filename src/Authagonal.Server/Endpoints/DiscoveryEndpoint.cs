namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (Authagonal.Core.Services.ITenantContext tenantContext) =>
        {
            var issuer = tenantContext.Issuer;

            return Results.Ok(new
            {
                issuer,
                authorization_endpoint = $"{issuer}/connect/authorize",
                token_endpoint = $"{issuer}/connect/token",
                userinfo_endpoint = $"{issuer}/connect/userinfo",
                jwks_uri = $"{issuer}/.well-known/openid-configuration/jwks",
                revocation_endpoint = $"{issuer}/connect/revocation",
                end_session_endpoint = $"{issuer}/connect/endsession",
                scopes_supported = new[] { "openid", "profile", "email", "offline_access" },
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
                code_challenge_methods_supported = new[] { "S256" }
            });
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
