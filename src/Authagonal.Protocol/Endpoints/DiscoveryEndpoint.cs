using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Protocol.Endpoints;

internal static class DiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapProtocolDiscoveryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", async (
            ITenantContext tenantContext,
            IScopeStore scopeStore,
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
                PushedAuthorizationRequestEndpoint = $"{issuer}/connect/par",
                ScopesSupported = scopesSupported,
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials"],
                SubjectTypesSupported = ["public"],
                IdTokenSigningAlgValuesSupported = ["RS256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post"],
                CodeChallengeMethodsSupported = ["S256"],
                ClaimsSupported = ["sub", "iss", "aud", "exp", "iat", "auth_time", "email", "email_verified", "name", "given_name", "family_name", "phone_number", "roles", "groups", "org_id"],
            }, ProtocolJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
