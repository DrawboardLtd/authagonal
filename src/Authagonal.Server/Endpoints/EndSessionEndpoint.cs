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
        IGrantStore grantStore,
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

        // Get subject ID before signing out (for back-channel + front-channel logout)
        var subjectId = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var sessionId = httpContext.User.FindFirst("sid")?.Value;

        // Collect front-channel logout URIs before signing out (grant lookup needs subject)
        var frontChannelUris = new List<string>();
        if (!string.IsNullOrEmpty(subjectId))
        {
            try
            {
                var grants = await grantStore.GetBySubjectAsync(subjectId);
                foreach (var clientIdGrant in grants.Select(g => g.ClientId).Distinct())
                {
                    var c = await clientStore.GetAsync(clientIdGrant, ct);
                    if (c?.FrontChannelLogoutUri is null) continue;
                    var uri = c.FrontChannelLogoutUri;
                    if (c.FrontChannelLogoutSessionRequired)
                    {
                        var sep = uri.Contains('?') ? '&' : '?';
                        uri = $"{uri}{sep}iss={Uri.EscapeDataString(tenantContext.Issuer)}";
                        if (!string.IsNullOrEmpty(sessionId))
                            uri += $"&sid={Uri.EscapeDataString(sessionId)}";
                    }
                    frontChannelUris.Add(uri);
                }
            }
            catch { /* fall through */ }
        }

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
                            var tokenSid = c.BackChannelLogoutSessionRequired ? sessionId : null;
                            var logoutToken = CreateBackChannelLogoutToken(tc.Issuer, clientIdGrant, subjectId, tokenSid, km);
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

        // Resolve the final redirect target (if any) by validating post_logout_redirect_uri against the client from id_token_hint.
        string? finalRedirect = null;
        if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri) && !string.IsNullOrWhiteSpace(idTokenHint))
        {
            var clientId = ExtractClientId(idTokenHint, keyManager, tenantContext.Issuer);
            if (clientId is not null)
            {
                var client = await clientStore.GetAsync(clientId, ct);
                if (client is not null &&
                    client.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.OrdinalIgnoreCase))
                {
                    finalRedirect = string.IsNullOrWhiteSpace(state)
                        ? postLogoutRedirectUri
                        : $"{postLogoutRedirectUri}{(postLogoutRedirectUri.Contains('?') ? '&' : '?')}state={Uri.EscapeDataString(state)}";
                }
            }
        }

        // If any clients registered front-channel logout URIs, render an HTML page with hidden iframes
        // so each client's logout endpoint is hit in the user's browser before redirecting/confirming.
        if (frontChannelUris.Count > 0)
        {
            var iframes = string.Join("\n", frontChannelUris.Select(u =>
                $"<iframe src=\"{System.Net.WebUtility.HtmlEncode(u)}\" style=\"display:none\"></iframe>"));

            string tail;
            if (!string.IsNullOrWhiteSpace(finalRedirect))
            {
                var escaped = System.Net.WebUtility.HtmlEncode(finalRedirect);
                tail = $"<script>setTimeout(function(){{ window.location.replace('{escaped}'); }}, 2000);</script>" +
                       $"<noscript><meta http-equiv=\"refresh\" content=\"2;url={escaped}\"></noscript>";
            }
            else
            {
                var msg = System.Net.WebUtility.HtmlEncode(localizer["EndSession_SignedOut"].Value);
                tail = $"<p>{msg}</p>";
            }

            var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Signed out</title></head>" +
                       $"<body>{iframes}{tail}</body></html>";

            return Results.Content(html, "text/html; charset=utf-8");
        }

        if (!string.IsNullOrWhiteSpace(finalRedirect))
            return Results.Redirect(finalRedirect);

        // No front-channel URIs and no validated redirect — just confirm logout.
        return TypedResults.Json(new MessageResponse { Message = localizer["EndSession_SignedOut"].Value }, AuthagonalJsonContext.Default.MessageResponse);
    }

    private static string? ExtractClientId(string idToken, Authagonal.Core.Services.IKeyManager keyManager, string issuer)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            // Read without full validation — we just need the client_id/aud claim.
            // The token may be expired (user is logging out), so skip lifetime validation.
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

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
        string issuer, string clientId, string subjectId, string? sessionId,
        Authagonal.Core.Services.IKeyManager keyManager)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subjectId,
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new { }
            },
            ["jti"] = Guid.NewGuid().ToString("N")
        };

        if (!string.IsNullOrEmpty(sessionId))
            claims["sid"] = sessionId;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            Claims = claims,
            SigningCredentials = keyManager.GetSigningCredentials()
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
