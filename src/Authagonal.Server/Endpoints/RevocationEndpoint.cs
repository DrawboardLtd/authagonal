using System.Text;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class RevocationEndpoint
{
    public static IEndpointRouteBuilder MapRevocationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/revocation", async (
            HttpContext httpContext,
            IProtocolTokenService tokenService,
            IClientStore clientStore,
            IRevokedTokenStore revokedTokenStore,
            IKeyManager keyManager,
            ITenantContext tenantContext,
            IClientSecretVerifier secretVerifier,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Through the shared client-authentication path rather than a private copy of it.
            // RFC 7009 §2.1 requires the client to authenticate "as described in Section 2.3 of
            // [RFC6749]", which is the same set of methods the token endpoint accepts — but this
            // endpoint only ever understood client_secret_basic/_post, so a client whose ONLY
            // registered credential is a key (private_key_jwt, which is what an FAPI or
            // mTLS-adjacent deployment registers) had no way to authenticate here at all. It was
            // locked out of revoking its own tokens: the endpoint that exists to shorten a
            // compromised token's life was the one it could not reach.
            var (client, authError) = await Authagonal.Protocol.Endpoints.ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier,
                (err, description) => JsonResults.OAuthError(err, description, 401), ct);
            if (authError is not null)
                return authError;

            var clientId = client!.ClientId;

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
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = tenantContext.Issuer,
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidAlgorithms = ["ES256"],
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

}
