using System.Security.Claims;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

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

            // F46: with no client (missing / unknown client_id) there is nothing to validate redirect_uri
            // against, so the error MUST be delivered directly — reflecting it to the attacker-supplied
            // redirect_uri would be an open redirect.
            if (string.IsNullOrWhiteSpace(clientId))
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "client_id is required", initialState);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "unauthorized_client", "Unknown client_id", initialState);

            if (!client.Enabled)
            {
                // F46: only reflect the error to a redirect_uri actually registered for this (disabled)
                // client; an unregistered one gets a direct error, never a bounce to an attacker URL.
                var safeRedirect = !string.IsNullOrWhiteSpace(initialRedirectUri)
                    && AuthorizeRequestSupport.IsRedirectUriRegistered(initialRedirectUri, client.RedirectUris)
                    ? initialRedirectUri
                    : null;
                return AuthorizeRequestSupport.BuildErrorRedirect(safeRedirect, "unauthorized_client", "Client is disabled", initialState);
            }

            IReadableRequestParameters source;
            if (!string.IsNullOrWhiteSpace(requestUri))
            {
                var record = await parService.LoadAsync(requestUri, clientId, ct);
                if (record is null)
                    return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "request_uri is unknown, expired, or already consumed", initialState);
                source = new ParRequestParameters(record.Parameters);
            }
            else
            {
                if (client.RequirePushedAuthorizationRequests)
                    return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "This client requires requests to be pushed via /connect/par", initialState);
                source = new QueryRequestParameters(httpContext.Request.Query);
            }

            var request = AuthorizeRequest.Read(source);

            if (AuthorizeRequestSupport.Validate(client, request) is { } validationError)
                return validationError;

            var (redirectUri, state, requestedScopes) = (request.RedirectUri!, request.State, request.RequestedScopes);

            // Check authentication
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                var authorizeRelativeUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";

                // RP-specified upstream IdP. The hint is an OIDC connection id understood
                // by the host's federation surface (/oidc/{conn}/login). We don't validate
                // it here — if it's unknown, that endpoint surfaces a 404 rather than
                // silently falling back to the login UI.
                var idpHint = source.Get("idp_hint");
                if (!string.IsNullOrWhiteSpace(idpHint))
                {
                    var federationLoginUrl = $"/oidc/{Uri.EscapeDataString(idpHint)}/login" +
                        $"?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}";
                    return Results.Redirect(federationLoginUrl);
                }

                var loginAppUrl = configuration["LoginAppUrl"] ?? "/login";
                var loginUrl = $"{loginAppUrl}?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}";

                var loginHint = source.Get("login_hint");
                if (!string.IsNullOrWhiteSpace(loginHint))
                    loginUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

                return Results.Redirect(loginUrl);
            }

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subjectId))
                return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "server_error", "Unable to determine user identity", state);

            // MFA enforcement (defence-in-depth): an MFA-enrolled user's session MUST have completed
            // MFA (local challenge) or have been established via an external IdP. After the login fix
            // every normal session satisfies this; a session lacking the marker is forced back through
            // authentication rather than being silently honoured for code issuance.
            var authenticatedUser = await userStore.GetAsync(subjectId, ct);
            if (authenticatedUser is { MfaEnabled: true } &&
                httpContext.User.FindFirst(CookieSignInHelper.MfaAuthenticatedClaim)?.Value != "true")
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var stepUpLoginUrl = configuration["LoginAppUrl"] ?? "/login";
                var stepUpReturn = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                return Results.Redirect($"{stepUpLoginUrl}?returnUrl={Uri.EscapeDataString(stepUpReturn)}");
            }

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
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "server_error", "User not found", state);

                try
                {
                    await provisioningOrchestrator.ProvisionAsync(provisionUser, client.ProvisioningApps, ct);
                }
                catch (ProvisioningException ex)
                {
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "access_denied",
                        ex.Reason ?? "User provisioning failed", state);
                }
            }

            // Resolve the subject through the host-registered resolver. The resolver reads
            // AuthUser from the user store, applies any session_max_exp cap captured in the
            // principal, and is the single place that maps identity → OidcSubject.
            var resolution = await subjectResolver.ResolveAsync(
                httpContext.User,
                new OidcSubjectResolutionContext(clientId, requestedScopes, request.Resources),
                ct);

            if (resolution is OidcSubjectResult.Rejected rejected)
            {
                var error = AuthorizeRequestSupport.MapRejectionError(rejected.Reason);
                return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, error, rejected.Description ?? "Subject not permitted", state);
            }

            var subject = ((OidcSubjectResult.Allowed)resolution).Subject;

            return await AuthorizeRequestSupport.IssueCodeAndRedirectAsync(
                authCodeService, parService, clientId, subject, request, requestUri, ct);
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        return app;
    }

    internal sealed class ConsentData
    {
        public List<string> Scopes { get; set; } = [];
        public DateTimeOffset ConsentedAt { get; set; }
    }
}
