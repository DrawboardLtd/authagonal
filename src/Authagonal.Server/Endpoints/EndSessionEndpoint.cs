using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class EndSessionEndpoint
{
    public static IEndpointRouteBuilder MapEndSessionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/endsession", HandleAsync).AllowAnonymous().WithTags("OAuth");
        app.MapPost("/connect/endsession", HandleAsync).AllowAnonymous().WithTags("OAuth");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IClientStore clientStore,
        KeyManager keyManager,
        IConfiguration config,
        CancellationToken ct)
    {
        var request = httpContext.Request;
        var idTokenHint = request.Query["id_token_hint"].FirstOrDefault()
            ?? request.Form["id_token_hint"].FirstOrDefault();
        var postLogoutRedirectUri = request.Query["post_logout_redirect_uri"].FirstOrDefault()
            ?? request.Form["post_logout_redirect_uri"].FirstOrDefault();
        var state = request.Query["state"].FirstOrDefault()
            ?? request.Form["state"].FirstOrDefault();

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (string.IsNullOrWhiteSpace(postLogoutRedirectUri))
            return Results.Ok(new { message = "You have been signed out." });

        // Validate post_logout_redirect_uri against the client from id_token_hint
        if (!string.IsNullOrWhiteSpace(idTokenHint))
        {
            var clientId = ExtractClientId(idTokenHint, keyManager, config["Issuer"]!);
            if (clientId is not null)
            {
                var client = await clientStore.FindByIdAsync(clientId, ct);
                if (client is not null &&
                    client.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.OrdinalIgnoreCase))
                {
                    var redirect = string.IsNullOrWhiteSpace(state)
                        ? postLogoutRedirectUri
                        : $"{postLogoutRedirectUri}{(postLogoutRedirectUri.Contains('?') ? '&' : '?')}state={Uri.EscapeDataString(state)}";
                    return Results.Redirect(redirect);
                }
            }
        }

        // If we can't validate the redirect, don't redirect — just confirm logout
        return Results.Ok(new { message = "You have been signed out." });
    }

    private static string? ExtractClientId(string idToken, KeyManager keyManager, string issuer)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            // Read without full validation — we just need the client_id/aud claim.
            // The token may be expired (user is logging out), so skip lifetime validation.
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

            var result = handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = false, // token may be expired
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true
            }).GetAwaiter().GetResult();

            if (!result.IsValid)
                return null;

            if (result.Claims.TryGetValue("client_id", out var cid))
                return cid?.ToString();

            if (result.Claims.TryGetValue("aud", out var aud))
                return aud?.ToString();

            return null;
        }
        catch
        {
            return null;
        }
    }
}
