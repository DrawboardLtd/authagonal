using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;

namespace Authagonal.Server.Endpoints;

public static class DiscoveryEndpoint
{
    /// <summary>
    /// The paths this metadata is served at. OIDC discovery is a superset of RFC 8414's
    /// authorization-server metadata, so one document satisfies both.
    /// </summary>
    /// <remarks>
    /// Publishing the RFC 8414 path matters for OAuth-only clients. The MCP authorization spec has
    /// clients resolve the authorization server via <c>oauth-authorization-server</c> FIRST, and an
    /// implementation is not obliged to fall back to OIDC discovery — so a host that answers only the
    /// OIDC path is undiscoverable to them even though its metadata is perfectly good.
    /// </remarks>
    private static readonly string[] MetadataPaths =
        ["/.well-known/openid-configuration", "/.well-known/oauth-authorization-server"];

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
                IdTokenSigningAlgValuesSupported = ["ES256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "client_secret_post", "private_key_jwt"],
                CodeChallengeMethodsSupported = ["S256"],
                BackchannelLogoutSupported = true,
                BackchannelLogoutSessionSupported = false,
                FrontchannelLogoutSupported = true,
                FrontchannelLogoutSessionSupported = true,
                ClaimsSupported = ["sub", "iss", "aud", "exp", "iat", "auth_time", "email", "email_verified", "name", "given_name", "family_name", "phone_number", "roles", "groups"],
                AuthorizationDetailsTypesSupported = authorityTypes,
            }, ProtocolJsonContext.Default.DiscoveryResponse);
        })
        .AllowAnonymous()
        .WithTags("Discovery");
        }

        return app;
    }
}
