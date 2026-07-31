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
                // ES256 alone, and RS256 deliberately absent: this server does not claim the RFC 9068
                // profile, whose §2.1 would require RS256 among the supported algorithms. The whole key
                // pipeline is EC — ProtocolSigningKeyOps purges any stored key that is not P-256, and
                // BuildJwksAsync hard-codes kty=EC — so advertising RS256 would advertise an algorithm
                // no key here can produce. One algorithm is the posture: every additional accepted one
                // is another way for a verifier to be talked into the wrong one. Stated in docs/index.md
                // so an integrator can discover it before writing a resource server that needs RS256.
                IdTokenSigningAlgValuesSupported = ["ES256"],
                // `none` is advertised because the token endpoint genuinely accepts it: a public
                // client (RequireClientSecret = false) authenticates with client_id alone, and
                // dynamic registration issues exactly such clients. Omitting it told every SPA and
                // native client that the only way in was a credential they cannot hold.
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post", "private_key_jwt", "none"],
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
