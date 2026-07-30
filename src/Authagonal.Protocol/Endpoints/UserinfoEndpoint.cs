using Authagonal.Core.Constants;
using Authagonal.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Endpoints;

internal static class UserinfoEndpoint
{
    public static IEndpointRouteBuilder MapProtocolUserinfoEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/userinfo", async (
            HttpContext httpContext,
            IKeyManager keyManager,
            ITenantContext tenantContext) =>
        {
            var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = authHeader["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
                return Results.Unauthorized();

            var issuer = tenantContext.Issuer;
            // JsonWebKey already implements SecurityKey — no conversion needed,
            // and this works for both EC and RSA without algorithm-specific handling.
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                // userinfo is the OP's own endpoint: any valid access token this OP issued (correct
                // issuer + signature + lifetime, with the openid scope) may call it, regardless of the
                // client it was minted for. So "any audience present" is correct here — unlike a
                // resource server, which pins its own audience.
                AudienceValidator = (audiences, _, _) => audiences?.Any() == true,
                ValidateLifetime = true,
                // Pin the algorithm, as the Server host's userinfo does. This validator was the one
                // inbound-token path with no ValidAlgorithms — a policy gap rather than a live hole
                // (the keys are EC, so no HMAC provider can be constructed over them), but the
                // guarantee belongs in this code rather than in a library default.
                ValidAlgorithms = ["ES256", "ES384", "ES512"],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, validationParams);

            if (!result.IsValid)
                return Results.Unauthorized();

            // Only an access token may call userinfo — see the Server host's equivalent check. An
            // id_token or a logout token carries the same issuer and signature but is not a credential.
            if (!TokenTypes.IsAccessToken(
                    (result.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.Typ,
                    result.Claims.ContainsKey))
                return Results.Unauthorized();

            // Userinfo returns whatever claims the access token carried that look user-identifying.
            // We do not re-engage the subject resolver — the token was minted from a resolved subject
            // and relying parties should call userinfo for a snapshot, not fresh re-resolution.
            // Hosts that want dynamic userinfo can wrap this endpoint.
            var claims = new Dictionary<string, object?>();

            foreach (var key in new[] { "sub", "email", "email_verified", "given_name", "family_name", "name", "phone_number", "org_id", "roles", "groups" })
            {
                if (result.Claims.TryGetValue(key, out var value) && value is not null)
                    claims[key] = value;
            }

            if (claims.Count == 0 || !claims.ContainsKey("sub"))
                return Results.Unauthorized();

            return Results.Ok(claims);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
