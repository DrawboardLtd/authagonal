using Authagonal.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Protocol.Endpoints;

internal static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapProtocolJwksEndpoint(this IEndpointRouteBuilder app)
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

            return TypedResults.Json(jwks, ProtocolJsonContext.Default.JwksDocument);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
