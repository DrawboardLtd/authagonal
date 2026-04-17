namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", async (
            Authagonal.Core.Services.ITenantContext tenantContext,
            Authagonal.Core.Stores.IScopeStore scopeStore,
            Microsoft.Extensions.Options.IOptions<Authagonal.Server.Services.AuthOptions> authOptions,
            CancellationToken ct) =>
        {
            var issuer = tenantContext.Issuer;

            var builtIn = new[] { "openid", "profile", "email", "offline_access" };
            string[] scopesSupported;
            try
            {
                var custom = await scopeStore.ListAsync(ct);
                scopesSupported = builtIn
                    .Concat(custom.Where(s => s.ShowInDiscoveryDocument).Select(s => s.Name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                scopesSupported = builtIn;
            }

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
                ScopesSupported = scopesSupported,
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code"],
                SubjectTypesSupported = ["public"],
                IdTokenSigningAlgValuesSupported = ["RS256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post"],
                CodeChallengeMethodsSupported = ["S256"],
                BackchannelLogoutSupported = true,
                BackchannelLogoutSessionSupported = false,
                FrontchannelLogoutSupported = true,
                FrontchannelLogoutSessionSupported = true,
                ClaimsSupported = ["sub", "iss", "aud", "exp", "iat", "auth_time", "email", "email_verified", "name", "given_name", "family_name", "phone_number", "roles", "groups"],
            }, AuthagonalJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
