using System.Security.Claims;
using Authagonal.Core.Constants;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

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
            IOidcProviderStore oidcProviderStore,
            ISsoDomainStore ssoDomainStore,
            UserStoreOidcSubjectResolver subjectResolver,
            // Explicit: an unresolvable service on a GET binds as a body parameter instead, which
            // surfaces as an opaque empty 400 rather than a missing-dependency error.
            [FromServices] IScopeRoleGate scopeRoleGate,
            ProtocolAuthorizationCodeService authCodeService,
            ProtocolPushedAuthorizationService parService,
            // Explicit for the same reason as IScopeRoleGate above: unresolvable services on a GET bind
            // as a body parameter and fail as an opaque 400.
            [FromServices] ITenantContext tenantContext,
            // Explicit for the same reason as the two above. Needed for MfaPolicy resolution, which a host
            // may override through IAuthHook.
            [FromServices] IEnumerable<IAuthHook> authHooks,
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
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request", "client_id is required", initialState, tenantContext.Issuer);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "unauthorized_client", "Unknown client_id", initialState, tenantContext.Issuer);

            if (!client.Enabled)
            {
                // F46: only reflect the error to a redirect_uri actually registered for this (disabled)
                // client; an unregistered one gets a direct error, never a bounce to an attacker URL.
                var safeRedirect = !string.IsNullOrWhiteSpace(initialRedirectUri)
                    && AuthorizeRequestSupport.IsRedirectUriRegistered(initialRedirectUri, client.RedirectUris)
                    ? initialRedirectUri
                    : null;
                return AuthorizeRequestSupport.BuildErrorRedirect(safeRedirect, "unauthorized_client", "Client is disabled", initialState, tenantContext.Issuer);
            }

            // OIDC Core §3.1.2.6 — refuse, don't ignore. RFC 9101 request objects are not implemented:
            // nothing reads `request`, and the only `request_uri` values honoured are the opaque URNs
            // this server's own PAR endpoint issued. Dropping either silently downgraded a JAR client
            // to its unsigned query string, which is the substitution the request object exists to
            // prevent. The prefix test keeps an expired pushed request reporting as expired.
            if (!string.IsNullOrWhiteSpace(httpContext.Request.Query["request"].FirstOrDefault()))
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "request_not_supported",
                    "Request objects (RFC 9101) are not supported", initialState, tenantContext.Issuer);

            if (!string.IsNullOrWhiteSpace(requestUri) &&
                !requestUri.StartsWith(Authagonal.Protocol.Services.ProtocolPushedAuthorizationService.RequestUriPrefix, StringComparison.Ordinal))
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "request_uri_not_supported",
                    "request_uri is supported only for pushed authorization requests", initialState, tenantContext.Issuer);

            // Scanned here, not only inside AuthorizeRequest.Read: on the PAR leg Read's source is the
            // pushed payload, so without this the query string is never examined for duplicates at all.
            if (AuthorizeRequest.FindDuplicatedQueryParameter(
                    new QueryRequestParameters(httpContext.Request.Query)) is { } duplicated)
                return AuthorizeRequestSupport.BuildErrorRedirect(null, "invalid_request",
                    $"Parameter '{duplicated}' appears more than once", initialState, tenantContext.Issuer);

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

            if (AuthorizeRequestSupport.Validate(client, request, tenantContext.Issuer) is { } validationError)
                return validationError;

            var (redirectUri, state, requestedScopes) = (request.RedirectUri!, request.State, request.RequestedScopes);

            // prompt=login (OIDC): the RP demands a fresh authentication even if a session exists. Used
            // by the guest share-link flow so the host doesn't silently reuse an SSO cookie that outlived
            // the caller's downstream session and claim the link as the wrong identity. When set, treat
            // an authenticated principal as unauthenticated and re-run login/federation.
            var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;

            var forceReauth = request.DemandsFreshAuthentication;

            // OIDC Core §3.1.2.1: when the elapsed time since the end-user last actively authenticated
            // exceeds max_age, the OP MUST actively re-authenticate them. The parameter was read
            // nowhere in the product, so `max_age=0` was answered from whatever cookie session existed
            // — up to the absolute session cap — with no re-authentication and no signal to the RP.
            // The auth_time needed to honour it has been minted on every sign-in since 0.11.0 and is
            // already read three lines below for the PAR loop-breaker; it was simply never compared.
            var sessionAuthTime = ReadAuthTime(httpContext.User);
            if (isAuthenticated && request.RequiresReauthentication(sessionAuthTime, DateTimeOffset.UtcNow))
                forceReauth = true;

            // prompt=login is satisfied only by a session established AFTER this request began. For a PAR
            // request the prompt rides the pushed payload (not the live query), so it can't be stripped on
            // the login round-trip; instead we require auth_time >= the record's CreatedAt. A pre-existing
            // or replayed cookie (auth_time < CreatedAt) fails and is forced to re-authenticate; a genuine
            // login during the round-trip passes, so the return trip issues a code instead of looping. The
            // reference (CreatedAt) is server-side, so a client can't forge its way past the demand.
            if (forceReauth && isAuthenticated && parCreatedAt is { } parCreated)
            {
                var authTimeClaim = httpContext.User.FindFirst(CookieSignInHelper.AuthTimeClaim)?.Value;
                if (long.TryParse(authTimeClaim, out var authTime) && authTime >= parCreated.ToUnixTimeSeconds())
                    forceReauth = false;
            }

            // Check authentication
            if (!isAuthenticated || forceReauth)
            {
                // OIDC Core §3.1.2.1: with prompt=none the OP MUST NOT display authentication UI —
                // it answers with an error instead. Everything below this line renders something.
                if (request.NoInteractionAllowed)
                    return AuthorizeRequestSupport.BuildErrorRedirect(
                        redirectUri, "login_required",
                        "The end-user is not authenticated and prompt=none forbids interaction",
                        state, tenantContext.Issuer);

                // prompt=login: drop any existing session before sending the user to log in, so a stale SSO
                // cookie can't be silently reused as the re-authenticated identity.
                if (forceReauth && isAuthenticated)
                    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                // Non-PAR: strip prompt from the returnUrl so the fresh session isn't force-re-authed again
                // when login/federation returns here (which would loop). A PAR request keeps its URL as-is —
                // its prompt lives in the pushed payload, and the auth_time >= CreatedAt check above is what
                // breaks its loop on return.
                var authorizeRelativeUrl = forceReauth && string.IsNullOrWhiteSpace(requestUri)
                    ? BuildRelativeUrlWithoutReauthDemands(httpContext.Request)
                    : $"{httpContext.Request.Path}{httpContext.Request.QueryString}";

                // RP-specified upstream IdP. The hint is an OIDC connection id understood
                // by the host's federation surface (/oidc/{conn}/login). We don't validate
                // it here — if it's unknown, that endpoint surfaces a 404 rather than
                // silently falling back to the login UI.
                var idpHint = source.Get("idp_hint");
                if (!string.IsNullOrWhiteSpace(idpHint))
                {
                    // A failed federation round redirects back here with error params appended.
                    // Re-federating would loop forever ("too many redirects") — return the error to
                    // the relying party instead, per OAuth (redirect_uri is already validated above).
                    // Read from the LIVE request query, not `source`: for a PAR request `source` is the
                    // pushed payload, which never carries the error the federation return appends to the
                    // authorize URL — reading `source` there would miss it and loop anyway.
                    var federationError = httpContext.Request.Query["error"].ToString();
                    if (!string.IsNullOrWhiteSpace(federationError))
                    {
                        var federationErrorDescription = httpContext.Request.Query["error_description"].ToString();
                        return AuthorizeRequestSupport.BuildErrorRedirect(
                            redirectUri, federationError,
                            string.IsNullOrWhiteSpace(federationErrorDescription) ? "Federated login failed" : federationErrorDescription,
                            state, tenantContext.Issuer);
                    }

                    // Connection interstitial: a connection can declare a login-app path to render
                    // BEFORE federating (e.g. a guest share-link's name/terms form). The page appends
                    // what it collects to the returnUrl query — the passthrough/provisioning source —
                    // and continues to /oidc/{conn}/login itself. Normally only unauthenticated entries
                    // reach here; an existing session CAN too when prompt=login forces re-auth (forceReauth),
                    // which is the intended behaviour — a forced re-auth should still see the interstitial.
                    var hintedConnection = await oidcProviderStore.GetAsync(idpHint, ct);
                    if (!string.IsNullOrWhiteSpace(hintedConnection?.InteractionPath))
                    {
                        var interactionAppUrl = configuration["LoginAppUrl"] ?? "/login";
                        var interactionUrl = $"{interactionAppUrl.TrimEnd('/')}{hintedConnection.InteractionPath}" +
                            $"?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}" +
                            $"&connection={Uri.EscapeDataString(idpHint)}";
                        return Results.Redirect(interactionUrl);
                    }

                    var federationLoginUrl = $"/oidc/{Uri.EscapeDataString(idpHint)}/login" +
                        $"?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}";
                    return Results.Redirect(federationLoginUrl);
                }

                var loginHint = source.Get("login_hint");

                // A hinted email whose domain is SSO-governed goes STRAIGHT to its IdP — the login
                // card would only 409 a password attempt for it anyway (sso_required), and product
                // flows that know the user (invite acceptance) shouldn't detour through an
                // interactive card. Mirrors /sso-check's resolution; loginHint rides along so the
                // IdP can prefill. Federation failures surface through the SAML/OIDC endpoints'
                // own error redirects, same as the card-initiated path.
                if (!string.IsNullOrWhiteSpace(loginHint) && loginHint.Contains('@'))
                {
                    var hintDomain = Authagonal.Core.Services.EmailDomain.Of(loginHint);
                    if (!string.IsNullOrWhiteSpace(hintDomain))
                    {
                        var ssoDomain = await ssoDomainStore.GetAsync(hintDomain, ct);
                        if (ssoDomain is not null)
                        {
                            var federationPath = ssoDomain.ProviderType.Equals("oidc", StringComparison.OrdinalIgnoreCase)
                                ? $"/oidc/{Uri.EscapeDataString(ssoDomain.ConnectionId)}/login"
                                : $"/saml/{Uri.EscapeDataString(ssoDomain.ConnectionId)}/login";
                            return Results.Redirect(
                                $"{federationPath}?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}" +
                                $"&loginHint={Uri.EscapeDataString(loginHint)}");
                        }
                    }
                }

                var loginAppUrl = configuration["LoginAppUrl"] ?? "/login";
                var loginUrl = $"{loginAppUrl}?returnUrl={Uri.EscapeDataString(authorizeRelativeUrl)}";

                if (!string.IsNullOrWhiteSpace(loginHint))
                    loginUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

                return Results.Redirect(loginUrl);
            }

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subjectId))
                return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "server_error", "Unable to determine user identity", state, tenantContext.Issuer);

            // MFA enforcement (defence-in-depth): an MFA-enrolled user's session MUST have completed
            // MFA (local challenge) or have been established via an external IdP. After the login fix
            // every normal session satisfies this; a session lacking the marker is forced back through
            // authentication rather than being silently honoured for code issuance.
            var authenticatedUser = await userStore.GetAsync(subjectId, ct);
            if (authenticatedUser is { MfaEnabled: true } &&
                httpContext.User.FindFirst(CookieSignInHelper.MfaAuthenticatedClaim)?.Value != "true")
            {
                // A step-up is interaction, so prompt=none cannot have it either. interaction_required
                // rather than login_required: the user IS authenticated, they just have not cleared a
                // second factor, and telling the RP "log in" would send it round a loop that ends here
                // again.
                if (request.NoInteractionAllowed)
                    return AuthorizeRequestSupport.BuildErrorRedirect(
                        redirectUri, "interaction_required",
                        "Multi-factor authentication is required and prompt=none forbids interaction",
                        state, tenantContext.Issuer);

                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var stepUpLoginUrl = configuration["LoginAppUrl"] ?? "/login";
                var stepUpReturn = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                return Results.Redirect($"{stepUpLoginUrl}?returnUrl={Uri.EscapeDataString(stepUpReturn)}");
            }

            // MfaPolicy.Required — "force enrolment for users without MFA" — enforced HERE, which is the only
            // place both the subject and the client are known for certain.
            //
            // It was previously evaluated in exactly two places, the password-login handler and the federated
            // MFA flow, and both derived the client (and therefore the policy) from the `returnUrl` QUERY
            // PARAMETER. When that was absent or named a different client the policy fell back to Disabled and
            // a full session cookie was signed with no second factor. The check above closes the ENROLLED case
            // regardless of returnUrl — an enrolled user is always challenged — but the enrolment-forcing case
            // rested entirely on an attacker-controllable value, so an unenrolled user reached a Required
            // client with a password-only session simply by arriving without the parameter.
            //
            // `client` here came from the validated authorization request, not from a query string.
            if (authenticatedUser is { MfaEnabled: false })
            {
                var effectivePolicy = await authHooks.RunResolveMfaPolicyAsync(
                    authenticatedUser.Id, authenticatedUser.Email, client.MfaPolicy, client.ClientId, ct);

                if (effectivePolicy == Core.Models.MfaPolicy.Required)
                {
                    // Enrolment is interaction, so prompt=none cannot have it — same reasoning as the step-up
                    // above, and the same code, because the RP's correct response is to retry interactively.
                    if (request.NoInteractionAllowed)
                        return AuthorizeRequestSupport.BuildErrorRedirect(
                            redirectUri, "interaction_required",
                            "Multi-factor authentication enrolment is required and prompt=none forbids interaction",
                            state, tenantContext.Issuer);

                    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    var enrolLoginUrl = configuration["LoginAppUrl"] ?? "/login";
                    var enrolReturn = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                    return Results.Redirect($"{enrolLoginUrl}?returnUrl={Uri.EscapeDataString(enrolReturn)}");
                }
            }

            // Per-user scope entitlement (Scope.AllowedRoles). Runs here because it is the first point
            // at which the subject is known, and BEFORE consent so the screen never offers a permission
            // this user cannot be granted. Scopes they do not qualify for are dropped, not refused —
            // a client whose staff surface is one scope among several must stay usable by everyone else.
            var entitledScopes = await scopeRoleGate.FilterAsync(requestedScopes, authenticatedUser?.Roles, ct);
            if (entitledScopes.Count < requestedScopes.Length)
            {
                if (entitledScopes.Count == 0)
                {
                    logger.LogWarning("Refusing all requested scopes for {SubjectId} on client {ClientId}: none are role-entitled",
                        subjectId, clientId);
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "access_denied",
                        "The user is not entitled to any of the requested scopes", state, tenantContext.Issuer);
                }

                logger.LogInformation("Dropping role-gated scopes for {SubjectId} on client {ClientId}: {Dropped}",
                    subjectId, clientId, string.Join(',', requestedScopes.Except(entitledScopes, StringComparer.Ordinal)));

                // Both, for the reason spelled out at the consent narrowing below: one is read by the
                // subject resolver, the other when the authorization code is minted.
                requestedScopes = [.. entitledScopes];
                request.RequestedScopes = requestedScopes;
            }

            // Check consent — because this client requires it, or because the RP asked for it.
            //
            // `prompt=consent` used to be accepted by Validate (it is in the OIDC registry, and an
            // unrecognised prompt is refused two lines away) and then read at exactly ONE place: inside
            // this block. `OAuthClient.RequireConsent` defaults to false and only dynamic registration
            // sets it, so for every admin-created or config-seeded client the parameter was parsed,
            // validated, and dropped — no screen, and no `consent_required` either. An RP whose whole
            // reason for sending it is to make the user re-affirm a permission got a silent code.
            if (client.RequireConsent || request.DemandsConsent)
            {
                var consentKey = $"consent:{subjectId}:{clientId}";

                // Built identically at every exit below, so it is written once.
                async Task<IResult> RedirectToConsentAsync()
                {
                    if (request.NoInteractionAllowed)
                        return AuthorizeRequestSupport.BuildErrorRedirect(
                            redirectUri, "consent_required",
                            "Consent is required and prompt=none forbids interaction",
                            state, tenantContext.Issuer);

                    // Record what is being OFFERED, server-side, before the user-agent is sent
                    // anywhere. The consent POST used to derive this from its own returnUrl field —
                    // caller-supplied, and a DIFFERENT parameter from the `scope` that drove what the
                    // screen actually rendered, so the displayed set and the recorded offered set
                    // could be made to diverge by construction. That matters because the authorize
                    // endpoint reads OfferedScopes as "already asked about" and SUPPRESSES the prompt
                    // for anything inside it: a wide offered set is a way to never be asked again.
                    // This is the only point at which the true offered set is known and not asserted
                    // by the caller.
                    await grantStore.StoreAsync(new Authagonal.Core.Models.PersistedGrant
                    {
                        Key = ConsentOfferKey(subjectId, clientId),
                        Type = PersistedGrantTypes.ConsentOffer,
                        ClientId = clientId,
                        SubjectId = subjectId,
                        Data = string.Join(" ", requestedScopes),
                        CreatedAt = DateTimeOffset.UtcNow,
                        // Long enough for a user to read the screen and decide; short enough that it
                        // cannot be harvested and replayed against a later, narrower request.
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                    }, ct);

                    var consentAppUrl = configuration["LoginAppUrl"] ?? "/login";
                    var authorizeUrl = $"{httpContext.Request.Path}{httpContext.Request.QueryString}";
                    return Results.Redirect(
                        $"{consentAppUrl.TrimEnd('/')}/consent?returnUrl={Uri.EscapeDataString(authorizeUrl)}&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(string.Join(" ", requestedScopes))}");
                }

                // prompt=consent: the RP asks for the screen regardless of what is stored. Combined
                // with none it is already refused as invalid_request, so this cannot reach the UI on a
                // no-interaction request.
                //
                // Satisfied ONCE per authorization request, not once per pass through this endpoint. The
                // consent POST sends the user-agent back to this same URL with `prompt` still on it, so an
                // unconditional re-prompt is an infinite redirect loop between the two endpoints — which is
                // what a RequireConsent client asking for prompt=consent already got.
                if (request.DemandsConsent)
                {
                    var promptKey = ConsentPromptKey(subjectId, clientId);
                    var promptSatisfied = await grantStore.GetAsync(promptKey, ct);
                    if (promptSatisfied is null || promptSatisfied.ExpiresAt <= DateTimeOffset.UtcNow)
                        return await RedirectToConsentAsync();

                    // Single-use: the next prompt=consent request is a new demand and gets its own screen.
                    await grantStore.RemoveAsync(promptKey, ct);
                }

                var existingConsent = await grantStore.GetAsync(consentKey, ct);
                if (existingConsent is null)
                    return await RedirectToConsentAsync();

                ConsentData? consentData;
                try
                {
                    consentData = System.Text.Json.JsonSerializer.Deserialize(existingConsent.Data, AuthagonalJsonContext.Default.ConsentData);
                }
                catch (Exception ex)
                {
                    // Consent data malformed — treat as not consented (require re-consent)
                    logger.LogWarning(ex, "Malformed consent data for key {ConsentKey}, requiring re-consent", consentKey);
                    await grantStore.RemoveAsync(consentKey, ct);
                    return await RedirectToConsentAsync();
                }

                var consentedScopes = new HashSet<string>(consentData?.Scopes ?? [], StringComparer.Ordinal);

                // What the user was SHOWN, which is not what they necessarily granted — the consent
                // screen lets them deselect. Keeping the two apart is what stops a client that keeps
                // asking for a scope the user declined from re-prompting on every single authorize.
                // Grants written before OfferedScopes existed carry none, so fall back to the granted
                // set — which reproduces the previous "re-prompt on anything not granted" behaviour.
                var offeredScopes = consentData?.OfferedScopes is { Count: > 0 } offered
                    ? new HashSet<string>(offered, StringComparer.Ordinal)
                    : consentedScopes;

                if (!requestedScopes.All(offeredScopes.Contains))
                {
                    // A scope the user has never been asked about. Ask.
                    await grantStore.RemoveAsync(consentKey, ct);
                    return await RedirectToConsentAsync();
                }

                // Everything requested has already been put to this user, so honour the answer they
                // gave: narrow the grant to what they approved instead of prompting again. Re-prompting
                // here would loop forever against a client that always requests its full scope set.
                // RFC 6749 §3.3 — the token response echoes the granted `scope`, so the client is told
                // it got less than it asked for.
                var grantedScopes = requestedScopes.Where(consentedScopes.Contains).ToArray();
                if (grantedScopes.Length == 0)
                {
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "access_denied",
                        "The user approved none of the requested scopes", state, tenantContext.Issuer);
                }

                // Both of these are read downstream — requestedScopes by the subject resolver, and
                // request.RequestedScopes when the authorization code is minted. Narrowing one without
                // the other would issue a token wider than the resolver was asked about.
                request.RequestedScopes = grantedScopes;
                requestedScopes = grantedScopes;
            }

            // Provision user into required downstream apps (TCC)
            if (client.ProvisioningApps.Count > 0)
            {
                var provisionUser = await userStore.GetAsync(subjectId, ct);
                if (provisionUser is null)
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "server_error", "User not found", state, tenantContext.Issuer);

                try
                {
                    await provisioningOrchestrator.ProvisionAsync(provisionUser, client.ProvisioningApps, ct);
                }
                catch (ProvisioningException ex)
                {
                    return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, "access_denied",
                        ex.Reason ?? "User provisioning failed", state, tenantContext.Issuer);
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
                return AuthorizeRequestSupport.BuildErrorRedirect(redirectUri, error, rejected.Description ?? "Subject not permitted", state, tenantContext.Issuer);
            }

            var subject = ((OidcSubjectResult.Allowed)resolution).Subject;

            return await AuthorizeRequestSupport.IssueCodeAndRedirectAsync(
                authCodeService, parService, clientId, subject, request, requestUri, tenantContext.Issuer, ct);
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        return app;
    }

    // Rebuild "{path}{query}" with the `prompt` param removed, so a prompt=login re-auth is honored once
    // and the login/federation return doesn't re-trigger it (which would loop). Reads the live query, so
    // this only helps the non-PAR flow — a PAR request carries prompt inside the pushed payload (which the
    // PAR record deliberately keeps across the login round-trip), so its loop is broken instead by the
    // auth_time >= record.CreatedAt check at /authorize, not by stripping.
    /// <summary>
    /// The same authorize URL with the re-authentication demands removed, for the round-trip through
    /// login.
    /// </summary>
    /// <remarks>
    /// Both <c>prompt=login</c> and <c>max_age</c> are demands the user is about to satisfy by
    /// actually authenticating; leaving them on the return URL re-triggers the demand and loops.
    /// <c>max_age</c> especially: <c>max_age=0</c> is unsatisfiable by any session that has existed
    /// for a measurable moment, so it would loop forever. Stripping is safe because this URL is built
    /// server-side, and the demand has already been met by the time it is used — the code issued on
    /// return carries a fresh <c>auth_time</c>, which is what lets the RP verify that for itself.
    /// PAR requests keep their URL: their parameters ride the pushed payload, and the
    /// <c>auth_time &gt;= CreatedAt</c> rule is what breaks their loop.
    /// </remarks>
    private static string BuildRelativeUrlWithoutReauthDemands(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        var qs = Microsoft.AspNetCore.Http.QueryString.Empty;
        foreach (var kv in request.Query)
        {
            if (string.Equals(kv.Key, "prompt", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "max_age", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var v in kv.Value)
                qs = qs.Add(kv.Key, v ?? string.Empty);
        }
        return $"{request.Path}{qs}";
    }

    /// <summary>The session's <c>auth_time</c> — when the user last actively authenticated.</summary>
    private static DateTimeOffset? ReadAuthTime(System.Security.Claims.ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst(CookieSignInHelper.AuthTimeClaim)?.Value;
        return long.TryParse(claim, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>
    /// Key of the record naming what this subject is currently being offered for this client.
    /// </summary>
    /// <remarks>
    /// Written here, read and consumed by <see cref="ConsentEndpoint"/>. Shared as a builder because the
    /// two endpoints agreeing on the format is what binds the consent screen to a pending request.
    /// </remarks>
    internal static string ConsentOfferKey(string subjectId, string clientId)
        => $"{PersistedGrantTypes.ConsentOffer}:{subjectId}:{clientId}";

    /// <summary>Key of the one-shot marker that a <c>prompt=consent</c> demand has been satisfied.</summary>
    internal static string ConsentPromptKey(string subjectId, string clientId)
        => $"{PersistedGrantTypes.ConsentPrompt}:{subjectId}:{clientId}";

    internal sealed class ConsentData
    {
        /// <summary>The scopes the user approved. This is the grant.</summary>
        public List<string> Scopes { get; set; } = [];

        /// <summary>
        /// The scopes the user was shown when they decided — a superset of <see cref="Scopes"/> whenever
        /// they deselected something. Recorded so a client that keeps requesting a declined scope
        /// prompts once rather than on every authorize. Null on grants written before per-scope consent.
        /// </summary>
        public List<string>? OfferedScopes { get; set; }

        public DateTimeOffset ConsentedAt { get; set; }
    }
}
