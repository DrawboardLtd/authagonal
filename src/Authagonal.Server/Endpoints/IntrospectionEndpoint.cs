using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

/// <summary>
/// OAuth 2.0 Token Introspection (RFC 7662).
/// Resource servers POST a token to check if it's active and get its claims.
/// Requires client authentication (client_secret_basic or client_secret_post).
/// </summary>
public static class IntrospectionEndpoint
{
    public static IEndpointRouteBuilder MapIntrospectionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/introspect", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("OAuth");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IClientStore clientStore,
        IGrantStore grantStore,
        IRevokedTokenStore revokedTokenStore,
        Authagonal.Core.Services.IKeyManager keyManager,
        Authagonal.Core.Services.ITenantContext tenantContext,
        PasswordHasher passwordHasher,
        CancellationToken ct)
    {
        var form = await httpContext.Request.ReadFormAsync(ct);

        // Authenticate the calling client (resource server)
        var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);
        if (string.IsNullOrWhiteSpace(clientId))
            return InactiveResponse();

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return InactiveResponse();

        if (client.RequireClientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                return InactiveResponse();

            var secretValid = client.ClientSecretHashes.Any(hash =>
            {
                var result = passwordHasher.VerifyPassword(clientSecret, hash);
                return result is PasswordVerifyResult.Success or PasswordVerifyResult.SuccessRehashNeeded;
            });

            if (!secretValid)
                return InactiveResponse();
        }

        var token = form["token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
            return InactiveResponse();

        // Try to validate as a JWT (access token / id token)
        var handler = new JsonWebTokenHandler();
        try
        {
            var jwt = handler.ReadJsonWebToken(token);

            var keys = keyManager.GetSecurityKeys()
                .Select(jwk =>
                {
                    var rsaKey = new RsaSecurityKey(new System.Security.Cryptography.RSAParameters
                    {
                        Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
                        Exponent = Base64UrlEncoder.DecodeBytes(jwk.E)
                    })
                    { KeyId = jwk.Kid };
                    return (SecurityKey)rsaKey;
                })
                .ToList();

            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = tenantContext.Issuer,
                ValidateIssuer = true,
                ValidateAudience = false, // introspection checks any token
                ValidateLifetime = true,
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            });

            if (!result.IsValid)
                return InactiveResponse();

            var jti = result.Claims.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(jti) && await revokedTokenStore.IsRevokedAsync(jti, ct))
                return InactiveResponse();

            var response = new Dictionary<string, object> { ["active"] = true };

            if (result.Claims.TryGetValue("sub", out var sub) && sub is not null)
                response["sub"] = sub;
            if (result.Claims.TryGetValue("client_id", out var cid) && cid is not null)
                response["client_id"] = cid;
            if (result.Claims.TryGetValue("scope", out var scope) && scope is not null)
                response["scope"] = scope;
            if (result.Claims.TryGetValue("iss", out var iss) && iss is not null)
                response["iss"] = iss;
            if (result.Claims.TryGetValue("exp", out var exp) && exp is not null)
                response["exp"] = exp;
            if (result.Claims.TryGetValue("iat", out var iat) && iat is not null)
                response["iat"] = iat;
            if (result.Claims.TryGetValue("aud", out var aud) && aud is not null)
                response["aud"] = aud;

            response["token_type"] = "Bearer";

            return Results.Ok(response);
        }
        catch
        {
            // Not a valid JWT — check if it's a refresh token (opaque)
            var grant = await grantStore.GetAsync(token, ct);
            if (grant is not null && grant.Type == "refresh_token" && grant.ConsumedAt is null && grant.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return Results.Ok(new Dictionary<string, object>
                {
                    ["active"] = true,
                    ["sub"] = grant.SubjectId ?? "",
                    ["client_id"] = grant.ClientId,
                    ["token_type"] = "refresh_token",
                    ["exp"] = grant.ExpiresAt.ToUnixTimeSeconds(),
                    ["iat"] = grant.CreatedAt.ToUnixTimeSeconds(),
                });
            }

            return InactiveResponse();
        }
    }

    private static IResult InactiveResponse() =>
        TypedResults.Json(new IntrospectionInactiveResponse(), AuthagonalJsonContext.Default.IntrospectionInactiveResponse);

    private static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..];
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                    return (Uri.UnescapeDataString(decoded[..colonIndex]), Uri.UnescapeDataString(decoded[(colonIndex + 1)..]));
            }
            catch (FormatException) { }
        }

        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }
}
