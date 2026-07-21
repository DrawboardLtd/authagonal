using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Protocol.Endpoints;

internal static class AuthorizeEndpoint
{
    public static IEndpointRouteBuilder MapProtocolAuthorizeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/authorize", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IOidcSubjectResolver subjectResolver,
            IOptions<AuthagonalProtocolOptions> protocolOptions,
            ProtocolAuthorizationCodeService authCodeService,
            ProtocolPushedAuthorizationService parService,
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
            // against, so the error MUST be delivered directly rather than reflected to an attacker URL.
            if (string.IsNullOrWhiteSpace(clientId))
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "client_id is required", initialState);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "unauthorized_client", "Unknown client_id", initialState);

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

            if (!client.Enabled)
            {
                // F46: don't reflect the error to an UNVALIDATED redirect_uri — an attacker who knows a
                // disabled client_id could otherwise bounce error+state to an arbitrary URL (open
                // redirect), since redirect_uri registration is normally checked later in Validate().
                // Only redirect to a redirect_uri actually registered for this client; else a direct error.
                var safeRedirect = !string.IsNullOrWhiteSpace(request.RedirectUri)
                    && AuthorizeRequestSupport.IsRedirectUriRegistered(request.RedirectUri, client.RedirectUris)
                    ? request.RedirectUri
                    : null;
                return AuthorizeRequestSupport.BuildErrorRedirect(safeRedirect, "unauthorized_client", "Client is disabled", request.State);
            }

            if (AuthorizeRequestSupport.Validate(client, request) is { } validationError)
                return validationError;

            // Authenticate — if the caller isn't already, either route them through the
            // hinted upstream IdP (for federation) or challenge the host's registered scheme.
            // The registered scheme may not be the HOST's default scheme (e.g. an API host whose
            // default is a bearer stack registering a purpose-built scheme just for this endpoint,
            // like bullclip's share-link handler) — so when the default-populated HttpContext.User
            // is anonymous, run the configured scheme EXPLICITLY before deciding to challenge.
            var authScheme = protocolOptions.Value.AuthenticationScheme;
            var principal = httpContext.User;
            AuthenticateResult? explicitAuth = null;
            if (principal.Identity?.IsAuthenticated != true && !string.IsNullOrEmpty(authScheme))
            {
                explicitAuth = await httpContext.AuthenticateAsync(authScheme);
                if (explicitAuth.Succeeded)
                    principal = explicitAuth.Principal;
            }

            // A hard authentication FAILURE (the scheme ran and rejected — e.g. an expired or revoked
            // share link) is distinct from NoResult (no credential presented). Retrying or challenging is
            // pointless once the credential was seen and refused, so return access_denied to the RP: its
            // error UI (and, for a federated RP, its loop-breaker) handle it, instead of falling through to
            // Challenge and emitting a raw 401 body the user would stare at mid-redirect. redirect_uri is
            // already validated above.
            if (principal.Identity?.IsAuthenticated != true && explicitAuth is { Failure: not null })
                return AuthorizeRequestSupport.BuildErrorRedirect(
                    request.RedirectUri, "access_denied",
                    string.IsNullOrWhiteSpace(explicitAuth.Failure.Message) ? "Authentication failed" : explicitAuth.Failure.Message,
                    request.State);

            if (principal.Identity?.IsAuthenticated != true)
            {
                var originalUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";

                // RP-specified upstream IdP. The hint is a connection id understood by
                // the host's federation surface (see Authagonal.Server's /oidc/{conn}/login).
                // We don't validate the connection here — if it's unknown, that endpoint
                // returns 404 and surfaces a real error rather than a silent fallback.
                var idpHint = source.Get("idp_hint");
                if (!string.IsNullOrWhiteSpace(idpHint))
                {
                    var federationLoginUrl = $"/oidc/{Uri.EscapeDataString(idpHint)}/login" +
                        $"?returnUrl={Uri.EscapeDataString(originalUrl)}";
                    return Results.Redirect(federationLoginUrl);
                }

                return Results.Challenge(
                    new AuthenticationProperties
                    {
                        RedirectUri = originalUrl
                    },
                    [authScheme]);
            }

            var context = new OidcSubjectResolutionContext(clientId, request.RequestedScopes, request.Resources);
            var resolved = await subjectResolver.ResolveAsync(principal, context, ct);

            if (resolved is OidcSubjectResult.Rejected rejected)
            {
                var err = AuthorizeRequestSupport.MapRejectionError(rejected.Reason);
                return AuthorizeRequestSupport.BuildErrorRedirect(request.RedirectUri, err, rejected.Description ?? "Subject not permitted", request.State);
            }

            var subject = ((OidcSubjectResult.Allowed)resolved).Subject;

            return await AuthorizeRequestSupport.IssueCodeAndRedirectAsync(
                authCodeService, parService, clientId, subject, request, requestUri, ct);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }
}
