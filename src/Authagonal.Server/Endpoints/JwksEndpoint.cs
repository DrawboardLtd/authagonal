using Authagonal.Core.Services;

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
            var keys = keyManager.GetSecurityKeys();

            var jwks = new JwksDocument
            {
                Keys = keys.Select(k => new JwkKey
                {
                    Kty = k.Kty,
                    Use = k.Use,
                    Kid = k.Kid,
                    Alg = k.Alg,
                    Crv = k.Crv,
                    X = k.X,
                    Y = k.Y,
                    N = k.N,
                    E = k.E,
                }).ToList()
            };

            return TypedResults.Json(jwks, AuthagonalJsonContext.Default.JwksDocument);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
