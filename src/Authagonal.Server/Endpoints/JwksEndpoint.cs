using Authagonal.Core.Services;
using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;

namespace Authagonal.Server.Endpoints;

public static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration/jwks", (IKeyManager keyManager, Microsoft.AspNetCore.Http.HttpResponse response) =>
        {
            // Edge/CDN-cacheable: JWKS advertises every non-expired key and rotation publishes the next
            // key days ahead, so a short shared cache never lacks a token's signing key.
            response.Headers.CacheControl = "public, max-age=3600";
            return TypedResults.Json(DiscoveryHelpers.BuildJwksDocument(keyManager), ProtocolJsonContext.Default.JwksDocument);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
