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
            HttpResponse response,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Edge/CDN-cacheable: per-tenant discovery metadata changes rarely.
            response.Headers.CacheControl = "public, max-age=3600";
            var issuer = tenantContext.Issuer;

            var scopesSupported = await DiscoveryHelpers.ResolveSupportedScopesAsync(scopeStore, ct);
            var authorityTypes = await DiscoveryHelpers.ResolveAuthorityTypesAsync(
                httpContext.RequestServices, ct);

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
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:token-exchange"],
                SubjectTypesSupported = ["public"],
                IdTokenSigningAlgValuesSupported = ["ES256"],
                // `none` is advertised because the token endpoint genuinely accepts it: a public
                // client (RequireClientSecret = false) authenticates with client_id alone, and
                // dynamic registration issues exactly such clients. Omitting it told every SPA and
                // native client that the only way in was a credential they cannot hold.
                //
                // Both lists come from ClientAuthentication, which is the code that implements them —
                // restating them here is how the two discovery documents drifted apart in the first place.
                TokenEndpointAuthMethodsSupported = ClientAuthentication.SupportedAuthMethods,
                TokenEndpointAuthSigningAlgValuesSupported = ClientAuthentication.SupportedAssertionAlgorithms,
                CodeChallengeMethodsSupported = ["S256"],
                AuthorizationResponseIssParameterSupported = true,
                // Stated rather than defaulted — see the members' own docs. Omitting these claimed
                // JAR-by-reference support and a fragment response mode, neither of which exists.
                RequestParameterSupported = false,
                RequestUriParameterSupported = false,
                ResponseModesSupported = ["query"],
                ClaimsSupported = ["sub", "iss", "aud", "exp", "iat", "auth_time", "email", "email_verified", "name", "given_name", "family_name", "phone_number", "roles", "groups", "org_id"],
                AuthorizationDetailsTypesSupported = authorityTypes,
            }, ProtocolJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
