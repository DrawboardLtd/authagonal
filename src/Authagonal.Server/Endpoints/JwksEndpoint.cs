using Authagonal.Core.Services;

namespace Authagonal.Server.Endpoints;

public static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration/jwks", (IKeyManager keyManager) =>
        {
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
