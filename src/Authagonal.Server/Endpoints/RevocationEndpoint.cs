using System.Text;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class RevocationEndpoint
{
    public static IEndpointRouteBuilder MapRevocationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/revocation", async (
            HttpContext httpContext,
            ITokenService tokenService,
            IClientStore clientStore,
            IRevokedTokenStore revokedTokenStore,
            IKeyManager keyManager,
            ITenantContext tenantContext,
            PasswordHasher passwordHasher,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Authenticate client
            var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

            if (string.IsNullOrWhiteSpace(clientId))
                return JsonResults.OAuthError("invalid_client", "client_id is required", 401);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return JsonResults.OAuthError("invalid_client", "Unknown client", 401);

            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return JsonResults.OAuthError("invalid_client", "client_secret is required", 401);

                var secretValid = client.ClientSecretHashes.Any(hash =>
                {
                    var result = passwordHasher.VerifyPassword(clientSecret, hash);
                    return result is PasswordVerifyResult.Success or PasswordVerifyResult.SuccessRehashNeeded;
                });

                if (!secretValid)
                    return JsonResults.OAuthError("invalid_client", "Invalid client credentials", 401);
            }

            var token = form["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
                return Results.Ok(); // Per RFC 7009, invalid tokens are not an error

            var tokenTypeHint = form["token_type_hint"].FirstOrDefault();

            // Try access_token first when hinted, otherwise try refresh_token first (historical default).
            if (tokenTypeHint == "access_token")
            {
                if (!await TryRevokeAccessTokenAsync(token, clientId, keyManager, tenantContext, revokedTokenStore, ct))
                    await tokenService.RevokeRefreshTokenAsync(token, clientId, ct);
            }
            else
            {
                var refreshRevoked = await tokenService.RevokeRefreshTokenAsync(token, clientId, ct);
                if (!refreshRevoked)
                    await TryRevokeAccessTokenAsync(token, clientId, keyManager, tenantContext, revokedTokenStore, ct);
            }

            // Per RFC 7009, always return 200 OK
            return Results.Ok();
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
    }

    private static async Task<bool> TryRevokeAccessTokenAsync(
        string token, string clientId, IKeyManager keyManager, ITenantContext tenantContext,
        IRevokedTokenStore revokedTokenStore, CancellationToken ct)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            var keys = keyManager.GetSecurityKeys()
                .Select(jwk =>
                {
                    var rsaKey = new RsaSecurityKey(new System.Security.Cryptography.RSAParameters
                    {
                        Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
                        Exponent = Base64UrlEncoder.DecodeBytes(jwk.E),
                    })
                    { KeyId = jwk.Kid };
                    return (SecurityKey)rsaKey;
                })
                .ToList();

            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = tenantContext.Issuer,
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60),
            });

            if (!result.IsValid) return false;

            // Per RFC 7009, the client revoking must own the token. Ignore silently if not.
            var tokenClientId = result.Claims.TryGetValue("client_id", out var cidObj) ? cidObj?.ToString() : null;
            if (!string.Equals(tokenClientId, clientId, StringComparison.Ordinal)) return false;

            var jti = result.Claims.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(jti)) return false;

            DateTimeOffset expiresAt;
            if (result.Claims.TryGetValue("exp", out var expObj) && expObj is not null &&
                long.TryParse(expObj.ToString(), out var expSeconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            }
            else
            {
                expiresAt = DateTimeOffset.UtcNow.AddHours(24);
            }

            await revokedTokenStore.AddAsync(jti, expiresAt, clientId, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..];
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    var id = Uri.UnescapeDataString(decoded[..colonIndex]);
                    var secret = Uri.UnescapeDataString(decoded[(colonIndex + 1)..]);
                    return (id, secret);
                }
            }
            catch (FormatException)
            {
                // Fall through
            }
        }

        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }
}
