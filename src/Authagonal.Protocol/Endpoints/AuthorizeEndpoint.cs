using Authagonal.Core.Services;
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
            ITenantContext tenantContext,
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
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "client_id is required", initialState, tenantContext.Issuer);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "unauthorized_client", "Unknown client_id", initialState, tenantContext.Issuer);

            IReadableRequestParameters source;
            DateTimeOffset? parCreatedAt = null;
            if (!string.IsNullOrWhiteSpace(requestUri))
            {
                var record = await parService.LoadAsync(requestUri, clientId, ct);
                if (record is null)
                    return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "request_uri is unknown, expired, or already consumed", initialState, tenantContext.Issuer);
                source = new ParRequestParameters(record.Parameters);
                parCreatedAt = record.CreatedAt;
            }
            else
            {
                if (client.RequirePushedAuthorizationRequests)
                    return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "This client requires requests to be pushed via /connect/par", initialState, tenantContext.Issuer);
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
                return AuthorizeRequestSupport.BuildErrorRedirect(safeRedirect, "unauthorized_client", "Client is disabled", request.State, tenantContext.Issuer);
            }

            if (AuthorizeRequestSupport.Validate(client, request, tenantContext.Issuer) is { } validationError)
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
                    request.State, tenantContext.Issuer);

            // Re-authentication demands. The 0.11.0 fix for prompt=login landed only in
            // Authagonal.Server, but this package ships and maps BOTH endpoints — so the shipped
            // Protocol host still answered `prompt=login` from whatever session already existed. The
            // guest share-link case that motivated the original fix (a stale SSO cookie outliving the
            // caller's downstream session and claiming the link as the wrong identity) applied here
            // unchanged. max_age was never honoured by either host.
            //
            // The demand is re-checked here rather than shared wholesale with Server because the two
            // hosts sign out and re-challenge differently: Server owns a login UI and a cookie scheme,
            // this host challenges whatever scheme the embedding application registered.
            var isAuthenticated = principal.Identity?.IsAuthenticated == true;
            var forceReauth = isAuthenticated
                && (request.DemandsFreshAuthentication
                    || request.RequiresReauthentication(ReadAuthTime(principal), DateTimeOffset.UtcNow));

            // A PAR request carries its prompt/max_age in the pushed payload, so they cannot be
            // stripped for the round-trip; the demand is instead satisfied by a session established
            // after the record was pushed. Same rule as Server, same server-side reference, so a
            // client cannot forge its way past it.
            if (forceReauth && parCreatedAt is { } parCreated
                && ReadAuthTime(principal) is { } established
                && established >= parCreated)
            {
                forceReauth = false;
            }

            // OIDC Core §3.1.2.1: prompt=none forbids the OP from displaying any authentication UI,
            // and a Challenge is exactly that. Both the re-auth demand below and the unauthenticated
            // branch after it end in one, so both answer the RP with a named error instead. Without
            // this, a silent-renewal iframe was served a login form it could never show the user.
            if (request.NoInteractionAllowed && (forceReauth || !isAuthenticated))
                return AuthorizeRequestSupport.BuildErrorRedirect(
                    request.RedirectUri, "login_required",
                    "The end-user is not authenticated and prompt=none forbids interaction",
                    request.State, tenantContext.Issuer);

            if (forceReauth)
            {
                // Drop the existing session first, so the challenge cannot be satisfied by the very
                // cookie the RP asked us not to reuse.
                if (!string.IsNullOrEmpty(authScheme))
                    await httpContext.SignOutAsync(authScheme);

                return Results.Challenge(
                    new AuthenticationProperties
                    {
                        // Stripped for the same reason Server strips them: the user is about to satisfy
                        // these demands by authenticating, and leaving them on the return URL re-triggers
                        // the demand. max_age=0 would otherwise never be satisfiable and loop forever.
                        RedirectUri = string.IsNullOrWhiteSpace(requestUri)
                            ? BuildUrlWithoutReauthDemands(httpContext.Request)
                            : $"{httpContext.Request.Path}{httpContext.Request.QueryString}",
                    },
                    [authScheme]);
            }

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
                return AuthorizeRequestSupport.BuildErrorRedirect(request.RedirectUri, err, rejected.Description ?? "Subject not permitted", request.State, tenantContext.Issuer);
            }

            var subject = ((OidcSubjectResult.Allowed)resolved).Subject;

            // Role-gated scopes are filtered here, as the Server host has always done.
            //
            // IScopeRoleGate's contract says it applies on every path that mints a token for a human,
            // and this endpoint skipped it — so a user holding none of a scope's AllowedRoles was
            // issued a code for it anyway, and the resulting access token carried the scope until the
            // first refresh (which does gate) quietly dropped it. Built over the scope store rather
            // than resolved from DI, matching ProtocolTokenService, so hosts that construct this by
            // hand keep working.
            var scopeStore = httpContext.RequestServices.GetService<IScopeStore>();
            if (scopeStore is not null)
            {
                var gate = new ScopeRoleGate(scopeStore);
                var permitted = await gate.FilterAsync(request.RequestedScopes, subject.Roles, ct);
                request.RequestedScopes = [.. permitted];
            }

            return await AuthorizeRequestSupport.IssueCodeAndRedirectAsync(
                authCodeService, parService, clientId, subject, request, requestUri, tenantContext.Issuer, ct);
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }

    /// <summary>The principal's <c>auth_time</c> — when it last actively authenticated.</summary>
    /// <remarks>
    /// Read as a claim rather than from a host-specific helper so this works for whatever scheme the
    /// embedding application registered. A host that never stamps <c>auth_time</c> yields null, which
    /// <see cref="AuthorizeRequest.RequiresReauthentication"/> treats as "cannot prove freshness" and
    /// therefore as requiring re-authentication — the safe direction for a demand.
    /// </remarks>
    private static DateTimeOffset? ReadAuthTime(System.Security.Claims.ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("auth_time")?.Value;
        return long.TryParse(claim, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The same URL without <c>prompt</c> / <c>max_age</c>, for the round-trip through login.</summary>
    private static string BuildUrlWithoutReauthDemands(HttpRequest request)
    {
        var qs = QueryString.Empty;
        foreach (var kv in request.Query)
        {
            if (string.Equals(kv.Key, "prompt", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "max_age", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var v in kv.Value)
                qs = qs.Add(kv.Key, v ?? string.Empty);
        }
        return $"{request.Path}{qs}";
    }
}
