using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Services;
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
            IUserStore userStore,
            IProvisioningOrchestrator provisioningOrchestrator,
            IConfiguration configuration,
            IGrantStore grantStore,
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

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return BuildErrorRedirect(redirectUri, "unauthorized_client", "Unknown client_id", state);

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BuildErrorRedirect(null, "invalid_request", "redirect_uri is required", state);

            if (!IsRedirectUriRegistered(redirectUri, client.RedirectUris))
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
                var authorizeRelativeUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                var loginUrl = $"{loginAppUrl}?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}";

                var loginHint = query["login_hint"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(loginHint))
                    loginUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

                return Results.Redirect(loginUrl);
            }

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subjectId))
                return BuildErrorRedirect(redirectUri, "server_error", "Unable to determine user identity", state);

            // Check consent (if required by this client)
            if (client.RequireConsent)
            {
                var consentKey = $"consent:{subjectId}:{clientId}";
                var existingConsent = await grantStore.GetAsync(consentKey, ct);
                if (existingConsent is null)
                {
                    // No consent yet — redirect to consent page
                    var consentAppUrl = configuration["LoginAppUrl"] ?? "/login";
                    var authorizeUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                    var consentUrl = $"{consentAppUrl.TrimEnd('/')}/consent?returnUrl={Uri.EscapeDataString(authorizeUrl)}&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(string.Join(" ", requestedScopes))}";
                    return Results.Redirect(consentUrl);
                }

                // Consent exists — verify scopes still match
                try
                {
                    var consentData = System.Text.Json.JsonSerializer.Deserialize<ConsentData>(existingConsent.Data);
                    var consentedScopes = new HashSet<string>(consentData?.Scopes ?? []);
                    if (!requestedScopes.All(s => consentedScopes.Contains(s)))
                    {
                        // New scopes requested — re-consent
                        await grantStore.RemoveAsync(consentKey, ct);
                        var consentAppUrl = configuration["LoginAppUrl"] ?? "/login";
                        var authorizeUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                        var consentUrl = $"{consentAppUrl.TrimEnd('/')}/consent?returnUrl={Uri.EscapeDataString(authorizeUrl)}&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(string.Join(" ", requestedScopes))}";
                        return Results.Redirect(consentUrl);
                    }
                }
                catch { /* consent data malformed — allow anyway */ }
            }

            // Provision user into required downstream apps (TCC)
            if (client.ProvisioningApps.Count > 0)
            {
                var user = await userStore.GetAsync(subjectId, ct);
                if (user is null)
                    return BuildErrorRedirect(redirectUri, "server_error", "User not found", state);

                try
                {
                    await provisioningOrchestrator.ProvisionAsync(user, client.ProvisioningApps, ct);
                }
                catch (ProvisioningException ex)
                {
                    return BuildErrorRedirect(redirectUri, "access_denied",
                        ex.Reason ?? "User provisioning failed", state);
                }
            }

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

    /// <summary>
    /// Compares redirect URIs using normalized form (scheme, host, port, path) to prevent bypass via
    /// implicit ports, trailing slashes, or encoding differences.
    /// </summary>
    private static bool IsRedirectUriRegistered(string redirectUri, IReadOnlyList<string> registeredUris)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requestedUri))
            return false;

        // Reject URIs with fragments (per OAuth spec)
        if (!string.IsNullOrEmpty(requestedUri.Fragment))
            return false;

        foreach (var registered in registeredUris)
        {
            if (!Uri.TryCreate(registered, UriKind.Absolute, out var registeredUri))
                continue;

            // Compare normalized components: scheme, host (case-insensitive), port, path (exact), query (exact)
            if (string.Equals(requestedUri.Scheme, registeredUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedUri.Host, registeredUri.Host, StringComparison.OrdinalIgnoreCase) &&
                requestedUri.Port == registeredUri.Port &&
                string.Equals(requestedUri.AbsolutePath, registeredUri.AbsolutePath, StringComparison.Ordinal) &&
                string.Equals(requestedUri.Query, registeredUri.Query, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    internal sealed class ConsentData
    {
        public List<string> Scopes { get; set; } = [];
        public DateTimeOffset ConsentedAt { get; set; }
    }
}
