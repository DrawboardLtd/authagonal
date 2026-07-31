using Authagonal.Core.Constants;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
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
        IClientSecretVerifier secretVerifier,
        CancellationToken ct)
    {
        var form = await httpContext.Request.ReadFormAsync(ct);

        // Authenticate the calling resource server through the shared path.
        //
        // RFC 7662 §2.1: "To prevent token scanning attacks, the endpoint MUST also require some form
        // of authorization to access this endpoint", repeated as a MUST in §4 — so a public client is
        // still refused (requireAuthenticatedClient), and a wrong secret is still §2.3's
        // authentication failure rather than a 200 {"active": false} that a resource server would read
        // as "that token is dead".
        //
        // What the private copy of this logic could not do was accept a client assertion, so a client
        // registered with a JWKS and no secret could not introspect at all. It is now the same set of
        // methods the token endpoint accepts.
        var (client, authError) = await Authagonal.Protocol.Endpoints.ClientAuthentication.AuthenticateAsync(
            httpContext, form, clientStore, secretVerifier,
            (err, _) => Results.Json(new { error = err }, statusCode: 401), ct,
            requireAuthenticatedClient: true);
        if (authError is not null)
            return authError;

        var clientId = client!.ClientId;

        var token = form["token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
            return InactiveResponse();

        // Try to validate as a JWT (access token / id token)
        var handler = new JsonWebTokenHandler();
        try
        {
            var jwt = handler.ReadJsonWebToken(token);

            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = tenantContext.Issuer,
                ValidateIssuer = true,
                ValidateAudience = false, // introspection checks any token
                ValidateLifetime = true,
                ValidAlgorithms = ["ES256"],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            });

            if (!result.IsValid)
                return InactiveResponse();

            // RFC 7662 introspects access and refresh tokens. An id_token or a logout token is neither,
            // and both are signed by the same key — reporting one as `active` would let a caller launder a
            // non-credential into something a resource server treats as live.
            if (!TokenTypes.IsAccessToken(
                    (result.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.Typ,
                    result.Claims.ContainsKey))
                return InactiveResponse();

            // RFC 7662 §4: the AS determines whether the token can be used at the resource server
            // making the call, and answers `active: false` when it cannot.
            //
            // Disclosure was never the issue — a JWT is self-describing to whoever holds it — but the
            // ANSWER was wrong: a resource server introspecting a token minted for a different
            // audience was told `active: true`, which is the AS confirming a token it should have
            // rejected. A resource server that (correctly) trusts introspection over its own audience
            // check therefore accepted it.
            //
            // A token with no audience at all is left alone: it is addressed to nobody in particular,
            // which the AS has no basis to narrow.
            var tokenAudiences = ReadAudiences(result.Claims);
            if (tokenAudiences.Count > 0
                && !tokenAudiences.Contains(client.ClientId, StringComparer.Ordinal)
                && !tokenAudiences.Any(a => client.Audiences.Contains(a, StringComparer.Ordinal)))
            {
                return InactiveResponse();
            }
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

            // Agentic claims — resource servers gate on these: act names the delegation chain
            // (RFC 8693 §4.1), authorization_details the fine-grained authority (RFC 9396).
            if (result.Claims.TryGetValue("act", out var act) && act is not null)
                response["act"] = act;
            if (result.Claims.TryGetValue("authorization_details", out var authorizationDetails) && authorizationDetails is not null)
                response["authorization_details"] = authorizationDetails;

            response["token_type"] = "Bearer";

            return Results.Ok(response);
        }
        catch
        {
            // Not a valid JWT — check if it's a refresh token (opaque)
            var grant = await grantStore.GetAsync(token, ct);
            if (grant is not null && grant.Type == "refresh_token" && grant.ConsumedAt is null &&
                grant.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
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

    /// <summary>The token's <c>aud</c>, which may be a single value or an array.</summary>
    private static List<string> ReadAudiences(IDictionary<string, object> claims)
    {
        if (!claims.TryGetValue("aud", out var aud) || aud is null) return [];

        return aud switch
        {
            string single => [single],
            IEnumerable<object> many => many.Select(a => a?.ToString()).Where(a => a is not null).Cast<string>().ToList(),
            _ => [aud.ToString()!],
        };
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
