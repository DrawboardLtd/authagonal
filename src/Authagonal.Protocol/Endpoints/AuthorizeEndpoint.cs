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

            if (string.IsNullOrWhiteSpace(clientId))
                return AuthorizeRequestSupport.BuildErrorRedirect(initialRedirectUri, "invalid_request", "client_id is required", initialState);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return AuthorizeRequestSupport.BuildErrorRedirect(initialRedirectUri, "unauthorized_client", "Unknown client_id", initialState);

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
                return AuthorizeRequestSupport.BuildErrorRedirect(request.RedirectUri, "unauthorized_client", "Client is disabled", request.State);

            if (AuthorizeRequestSupport.Validate(client, request) is { } validationError)
                return validationError;

            // Authenticate — if the caller isn't already, either route them through the
            // hinted upstream IdP (for federation) or challenge the host's registered scheme.
            var authScheme = protocolOptions.Value.AuthenticationScheme;
            if (httpContext.User.Identity?.IsAuthenticated != true)
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
            var resolved = await subjectResolver.ResolveAsync(httpContext.User, context, ct);

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
