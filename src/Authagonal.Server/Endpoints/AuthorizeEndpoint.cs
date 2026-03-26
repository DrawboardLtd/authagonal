using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints;

public static class AuthorizeEndpoint
{
    public static IEndpointRouteBuilder MapAuthorizeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/authorize", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IConfiguration configuration,
            AuthorizationCodeService authCodeService,
            CancellationToken ct) =>
        {
            var query = httpContext.Request.Query;

            var clientId = query["client_id"].FirstOrDefault();
            var redirectUri = query["redirect_uri"].FirstOrDefault();
            var responseType = query["response_type"].FirstOrDefault();
            var scope = query["scope"].FirstOrDefault();
            var state = query["state"].FirstOrDefault();
            var codeChallenge = query["code_challenge"].FirstOrDefault();
            var codeChallengeMethod = query["code_challenge_method"].FirstOrDefault();
            var nonce = query["nonce"].FirstOrDefault();

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(clientId))
                return BuildErrorRedirect(redirectUri, "invalid_request", "client_id is required", state);

            var client = await clientStore.FindByIdAsync(clientId, ct);
            if (client is null)
                return BuildErrorRedirect(redirectUri, "unauthorized_client", "Unknown client_id", state);

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BuildErrorRedirect(null, "invalid_request", "redirect_uri is required", state);

            if (!client.RedirectUris.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
                return BuildErrorRedirect(null, "invalid_request", "redirect_uri is not registered for this client", state);

            if (string.IsNullOrWhiteSpace(responseType) || responseType != "code")
                return BuildErrorRedirect(redirectUri, "unsupported_response_type", "Only response_type=code is supported", state);

            if (string.IsNullOrWhiteSpace(scope))
                return BuildErrorRedirect(redirectUri, "invalid_scope", "scope is required", state);

            var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
            if (invalidScopes.Length > 0)
                return BuildErrorRedirect(redirectUri, "invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}", state);

            // Validate PKCE
            if (client.RequirePkce)
            {
                if (string.IsNullOrWhiteSpace(codeChallenge))
                    return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge is required", state);

                if (string.IsNullOrWhiteSpace(codeChallengeMethod) || codeChallengeMethod != "S256")
                    return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge_method must be S256", state);
            }

            // Check authentication
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                var loginAppUrl = configuration["LoginAppUrl"] ?? "/login";
                var fullAuthorizeUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.Path}{httpContext.Request.QueryString}";
                var loginUrl = $"{loginAppUrl}?returnUrl={Uri.EscapeDataString(fullAuthorizeUrl)}";

                var loginHint = query["login_hint"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(loginHint))
                    loginUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

                return Results.Redirect(loginUrl);
            }

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subjectId))
                return BuildErrorRedirect(redirectUri, "server_error", "Unable to determine user identity", state);

            // Create authorization code
            var code = await authCodeService.CreateCodeAsync(
                clientId,
                subjectId,
                redirectUri,
                requestedScopes.ToList(),
                codeChallenge,
                codeChallengeMethod,
                nonce,
                ct);

            // Build redirect URI with code and state
            var uriBuilder = new UriBuilder(redirectUri);
            var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);
            queryParams["code"] = code;
            if (!string.IsNullOrWhiteSpace(state))
                queryParams["state"] = state;
            uriBuilder.Query = queryParams.ToString();

            return Results.Redirect(uriBuilder.ToString());
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        return app;
    }

    private static IResult BuildErrorRedirect(string? redirectUri, string error, string errorDescription, string? state)
    {
        // If we can't redirect (no valid redirect_uri), return a direct error
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return Results.BadRequest(new { error, error_description = errorDescription });
        }

        var uriBuilder = new UriBuilder(redirectUri);
        var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);
        queryParams["error"] = error;
        queryParams["error_description"] = errorDescription;
        if (!string.IsNullOrWhiteSpace(state))
            queryParams["state"] = state;
        uriBuilder.Query = queryParams.ToString();

        return Results.Redirect(uriBuilder.ToString());
    }
}
