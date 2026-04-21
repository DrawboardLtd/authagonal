using System.Security.Claims;
using System.Web;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
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
            UserStoreOidcSubjectResolver subjectResolver,
            ProtocolAuthorizationCodeService authCodeService,
            ProtocolPushedAuthorizationService parService,
            ILogger<ProtocolAuthorizationCodeService> logger,
            CancellationToken ct) =>
        {
            var clientId = httpContext.Request.Query["client_id"].FirstOrDefault();
            var initialState = httpContext.Request.Query["state"].FirstOrDefault();
            var requestUri = httpContext.Request.Query["request_uri"].FirstOrDefault();
            // Pre-lookup redirect-back target — only honoured for non-PAR flow, since a PAR
            // request keeps redirect_uri inside the pushed payload.
            var initialRedirectUri = string.IsNullOrWhiteSpace(requestUri)
                ? httpContext.Request.Query["redirect_uri"].FirstOrDefault()
                : null;

            if (string.IsNullOrWhiteSpace(clientId))
                return BuildErrorRedirect(initialRedirectUri, "invalid_request", "client_id is required", initialState);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return BuildErrorRedirect(initialRedirectUri, "unauthorized_client", "Unknown client_id", initialState);

            if (!client.Enabled)
                return BuildErrorRedirect(initialRedirectUri, "unauthorized_client", "Client is disabled", initialState);

            Dictionary<string, string[]>? pushedParams = null;
            if (!string.IsNullOrWhiteSpace(requestUri))
            {
                var record = await parService.LoadAsync(requestUri, clientId, ct);
                if (record is null)
                    return BuildErrorRedirect(null, "invalid_request", "request_uri is unknown, expired, or already consumed", initialState);
                pushedParams = record.Parameters;
            }
            else if (client.RequirePushedAuthorizationRequests)
            {
                return BuildErrorRedirect(null, "invalid_request", "This client requires requests to be pushed via /connect/par", initialState);
            }

            string? Get(string key) => pushedParams is not null
                ? (pushedParams.TryGetValue(key, out var values) ? values.FirstOrDefault() : null)
                : httpContext.Request.Query[key].FirstOrDefault();
            IEnumerable<string> GetAll(string key) => pushedParams is not null
                ? (pushedParams.TryGetValue(key, out var values) ? values : [])
                : httpContext.Request.Query[key].Where(v => v is not null).Cast<string>();

            var redirectUri = Get("redirect_uri");
            var responseType = Get("response_type");
            var scope = Get("scope");
            var state = Get("state");
            var codeChallenge = Get("code_challenge");
            var codeChallengeMethod = Get("code_challenge_method");
            var nonce = Get("nonce");
            var resources = GetAll("resource").Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();

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

            // RFC 8707: validate resource indicators against the client's registered audiences.
            // Each resource must be an absolute URI without a fragment, and must be in client.Audiences.
            foreach (var resource in resources)
            {
                if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || !string.IsNullOrEmpty(resourceUri.Fragment))
                    return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not a valid absolute URI", state);

                if (!client.Audiences.Contains(resource, StringComparer.Ordinal))
                    return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not registered for this client", state);
            }

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

                var loginHint = Get("login_hint");
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
                    var consentData = System.Text.Json.JsonSerializer.Deserialize(existingConsent.Data, AuthagonalJsonContext.Default.ConsentData);
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
                catch (Exception ex)
                {
                    // Consent data malformed — treat as not consented (require re-consent)
                    logger.LogWarning(ex, "Malformed consent data for key {ConsentKey}, requiring re-consent", consentKey);
                    await grantStore.RemoveAsync(consentKey, ct);
                    var consentAppUrl2 = configuration["LoginAppUrl"] ?? "/login";
                    var authorizeUrl2 = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                    var consentUrl2 = $"{consentAppUrl2.TrimEnd('/')}/consent?returnUrl={Uri.EscapeDataString(authorizeUrl2)}&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(string.Join(" ", requestedScopes))}";
                    return Results.Redirect(consentUrl2);
                }
            }

            // Provision user into required downstream apps (TCC)
            if (client.ProvisioningApps.Count > 0)
            {
                var provisionUser = await userStore.GetAsync(subjectId, ct);
                if (provisionUser is null)
                    return BuildErrorRedirect(redirectUri, "server_error", "User not found", state);

                try
                {
                    await provisioningOrchestrator.ProvisionAsync(provisionUser, client.ProvisioningApps, ct);
                }
                catch (ProvisioningException ex)
                {
                    return BuildErrorRedirect(redirectUri, "access_denied",
                        ex.Reason ?? "User provisioning failed", state);
                }
            }

            // Resolve the subject through the host-registered resolver. The resolver reads
            // AuthUser from the user store, applies any session_max_exp cap captured in the
            // principal, and is the single place that maps identity → OidcSubject.
            var resolution = await subjectResolver.ResolveAsync(
                httpContext.User,
                new OidcSubjectResolutionContext(clientId, requestedScopes, resources),
                ct);

            if (resolution is OidcSubjectResult.Rejected rejected)
            {
                var error = rejected.Reason switch
                {
                    OidcRejection.LoginRequired => "login_required",
                    OidcRejection.ConsentRequired => "consent_required",
                    OidcRejection.AccountSelectionRequired => "account_selection_required",
                    _ => "access_denied",
                };
                return BuildErrorRedirect(redirectUri, error, rejected.Description ?? "Subject not permitted", state);
            }

            var subject = ((OidcSubjectResult.Allowed)resolution).Subject;

            var code = await authCodeService.CreateCodeAsync(
                clientId,
                subject,
                redirectUri,
                requestedScopes.ToList(),
                codeChallenge,
                codeChallengeMethod,
                nonce,
                resources.Length > 0 ? resources : null,
                ct);

            if (!string.IsNullOrWhiteSpace(requestUri))
                await parService.RemoveAsync(requestUri, ct);

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
