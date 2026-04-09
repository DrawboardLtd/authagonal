using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Localization;
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
        Authagonal.Core.Services.IKeyManager keyManager,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var request = httpContext.Request;
        var hasForm = request.HasFormContentType;
        var idTokenHint = request.Query["id_token_hint"].FirstOrDefault()
            ?? (hasForm ? request.Form["id_token_hint"].FirstOrDefault() : null);
        var postLogoutRedirectUri = request.Query["post_logout_redirect_uri"].FirstOrDefault()
            ?? (hasForm ? request.Form["post_logout_redirect_uri"].FirstOrDefault() : null);
        var state = request.Query["state"].FirstOrDefault()
            ?? (hasForm ? request.Form["state"].FirstOrDefault() : null);

        // Get subject ID before signing out (for back-channel logout)
        var subjectId = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Trigger back-channel logout notifications (fire and forget)
        if (!string.IsNullOrEmpty(subjectId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = httpContext.RequestServices.CreateScope();
                    var grantStore = scope.ServiceProvider.GetRequiredService<IGrantStore>();
                    var cs = scope.ServiceProvider.GetRequiredService<IClientStore>();
                    var km = scope.ServiceProvider.GetRequiredService<Authagonal.Core.Services.IKeyManager>();
                    var tc = scope.ServiceProvider.GetRequiredService<Authagonal.Core.Services.ITenantContext>();
                    var hcf = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BackChannelLogout");

                    var grants = await grantStore.GetBySubjectAsync(subjectId);
                    foreach (var clientIdGrant in grants.Select(g => g.ClientId).Distinct())
                    {
                        var c = await cs.GetAsync(clientIdGrant);
                        if (c?.BackChannelLogoutUri is null) continue;

                        try
                        {
                            var logoutToken = CreateBackChannelLogoutToken(tc.Issuer, clientIdGrant, subjectId, km);
                            var client = hcf.CreateClient("BackChannelLogout");
                            client.Timeout = TimeSpan.FromSeconds(10);
                            await client.PostAsync(c.BackChannelLogoutUri,
                                new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken }));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Back-channel logout failed for client {ClientId}", clientIdGrant);
                        }
                    }

                    await grantStore.RemoveAllBySubjectAsync(subjectId);
                }
                catch { /* best effort */ }
            });
        }

        if (string.IsNullOrWhiteSpace(postLogoutRedirectUri))
            return Results.Ok(new { message = localizer["EndSession_SignedOut"].Value });

        // Validate post_logout_redirect_uri against the client from id_token_hint
        if (!string.IsNullOrWhiteSpace(idTokenHint))
        {
            var clientId = ExtractClientId(idTokenHint, keyManager, tenantContext.Issuer);
            if (clientId is not null)
            {
                var client = await clientStore.GetAsync(clientId, ct);
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
        return Results.Ok(new { message = localizer["EndSession_SignedOut"].Value });
    }

    private static string? ExtractClientId(string idToken, Authagonal.Core.Services.IKeyManager keyManager, string issuer)
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

    private static string CreateBackChannelLogoutToken(
        string issuer, string clientId, string subjectId, Authagonal.Core.Services.IKeyManager keyManager)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subjectId,
                ["events"] = new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] = new { }
                },
                ["jti"] = Guid.NewGuid().ToString("N")
            },
            SigningCredentials = keyManager.GetSigningCredentials()
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
