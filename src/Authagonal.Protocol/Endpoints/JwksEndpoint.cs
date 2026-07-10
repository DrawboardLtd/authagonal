using Authagonal.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Protocol.Endpoints;

internal static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapProtocolJwksEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration/jwks", (IKeyManager keyManager, HttpResponse response) =>
        {
            // Edge/CDN-cacheable: all non-expired keys are advertised and the next key is published days
            // ahead of use, so a short shared cache is always safe.
            response.Headers.CacheControl = "public, max-age=3600";
            return TypedResults.Json(DiscoveryHelpers.BuildJwksDocument(keyManager), ProtocolJsonContext.Default.JwksDocument);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
