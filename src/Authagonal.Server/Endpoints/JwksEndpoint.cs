using Authagonal.Core.Services;

namespace Authagonal.Server.Endpoints;

public static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration/jwks", (IKeyManager keyManager) =>
        {
            var keys = keyManager.GetSecurityKeys();

            var jwks = new
            {
                keys = keys.Select(k => new
                {
                    kty = k.Kty,
                    use = k.Use,
                    kid = k.Kid,
                    alg = k.Alg,
                    n = k.N,
                    e = k.E
                })
            };

            return Results.Ok(jwks);
        })
        .AllowAnonymous()
        .WithTags("Discovery");

        return app;
    }
}
