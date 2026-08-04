using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;

namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    /// <summary>
    /// The paths this metadata is served at — shared with the Protocol host, which published only the OIDC
    /// one. See <see cref="Authagonal.Protocol.Endpoints.DiscoveryHelpers.MetadataPaths"/>.
    /// </summary>
    private static string[] MetadataPaths => Authagonal.Protocol.Endpoints.DiscoveryHelpers.MetadataPaths;

    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var path in MetadataPaths)
        {
        app.MapGet(path, async (
            Authagonal.Core.Services.ITenantContext tenantContext,
            Authagonal.Core.Stores.IScopeStore scopeStore,
            Microsoft.Extensions.Options.IOptions<Authagonal.Server.Services.AuthOptions> authOptions,
            Microsoft.AspNetCore.Http.HttpResponse response,
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
                RevocationEndpoint = $"{issuer}/connect/revocation",
                IntrospectionEndpoint = $"{issuer}/connect/introspect",
                EndSessionEndpoint = $"{issuer}/connect/endsession",
                DeviceAuthorizationEndpoint = $"{issuer}/connect/deviceauthorization",
                RegistrationEndpoint = authOptions.Value.DynamicClientRegistrationEnabled ? $"{issuer}/connect/register" : null,
                PushedAuthorizationRequestEndpoint = $"{issuer}/connect/par",
                ScopesSupported = scopesSupported,
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code", "urn:ietf:params:oauth:grant-type:token-exchange"],
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
                BackchannelLogoutSupported = true,
                // The OP puts `sid` in every ID token and in every Logout Token, which IS
                // session-based back-channel logout — advertising false told relying parties not to
                // expect the sid they were being sent, so a conforming RP ignored it and could not
                // correlate the logout to a session.
                BackchannelLogoutSessionSupported = true,
                FrontchannelLogoutSupported = true,
                FrontchannelLogoutSessionSupported = true,
                ClaimsSupported = Authagonal.Protocol.Endpoints.DiscoveryHelpers.ClaimsSupported,
                AuthorizationDetailsTypesSupported = authorityTypes,
            }, ProtocolJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("Discovery");
        }

        return app;
    }
}
