using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints;

public static class ConsentEndpoint
{
    public static IEndpointRouteBuilder MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/consent/info", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IScopeStore scopeStore,
            IGrantStore grantStore,
            string client_id,
            CancellationToken ct) =>
        {
            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(subjectId))
                return (IResult)Results.Unauthorized();

            var client = await clientStore.GetAsync(client_id, ct);
            if (client is null)
                return (IResult)TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            // The scopes come from the OFFER the authorize endpoint recorded, never from the caller.
            //
            // This endpoint used to render entirely from two query parameters, `client_id` and `scope`,
            // which the login app took straight off its own query string. So the consent card — the IdP's
            // own origin, the IdP's own styling, a real registered client's name, description and logo —
            // was a function of the link the user clicked rather than of any authorization request the
            // server was holding. A crafted link could show a trusted client's identity above an
            // attacker-chosen permission list, and the POST behind it recorded a real five-year grant.
            //
            // Requiring the offer record collapses that: with no pending request there is nothing to
            // render, and the scope list is the one the server itself computed (after role entitlement
            // filtering) rather than one asserted by whoever composed the URL.
            var requestedScopes = await ReadLiveOfferAsync(grantStore, subjectId, client_id, ct);
            if (requestedScopes is null)
                return (IResult)TypedResults.Json(
                    new ErrorInfoResponse { Error = "no_pending_consent_request" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Resolved from the registry so the screen shows the wording whoever registered the scope
            // chose. An unregistered scope yields nulls and the login app falls back — better than this
            // endpoint inventing a label, which it has no basis to do.
            var details = new List<ConsentScopeInfo>(requestedScopes.Length);
            foreach (var name in requestedScopes)
            {
                var registered = await scopeStore.GetAsync(name, ct);
                details.Add(new ConsentScopeInfo
                {
                    Name = name,
                    DisplayName = registered?.DisplayName,
                    Description = registered?.Description,
                    Emphasize = registered?.Emphasize ?? false,
                    Required = registered?.Required ?? false,
                    Group = registered?.Group,
                });
            }

            return (IResult)TypedResults.Json(new ConsentInfoResponse
            {
                ClientId = client.ClientId,
                ClientName = client.ClientName,
                Description = client.Description,
                ClientUri = client.ClientUri,
                LogoUri = client.LogoUri,
                Scopes = requestedScopes,
                ScopeDetails = details.ToArray(),
            }, AuthagonalJsonContext.Default.ConsentInfoResponse);
        })
        // The consent screen is only ever rendered to a signed-in user, and this endpoint answers
        // "does client X exist, and what is it called" to anyone who asks. Anonymous access made it a
        // client-enumeration oracle over the whole registry — names, descriptions, logo and home URIs
        // — which is reconnaissance for a consent-phishing page that impersonates a real client the
        // user has seen before. Its three sibling endpoints already require authorization.
        .RequireAuthorization().RequireOwnOrigin();

        app.MapPost("/consent", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            ITenantContext tenantContext,
            ConsentRequest request,
            CancellationToken ct) =>
        {
            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subjectId))
                return Results.Unauthorized();

            var client = await clientStore.GetAsync(request.ClientId, ct);
            if (client is null)
                return TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            if (request.Decision == "deny")
            {
                // Find the redirect_uri from the returnUrl query params
                if (!string.IsNullOrEmpty(request.ReturnUrl))
                {
                    var uri = new Uri($"https://placeholder{request.ReturnUrl}");
                    var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var redirectUri = queryParams["redirect_uri"];
                    var state = queryParams["state"];

                    // The redirect_uri here was parsed out of the CALLER-supplied returnUrl, so it is
                    // attacker-chosen. It must be one this client registered. Emitting an OAuth error to an
                    // unregistered URI is an open redirect on the IdP origin and violates RFC 6749
                    // §4.1.2.1 ("MUST NOT automatically redirect the user-agent to the invalid redirection
                    // URI"). /connect/authorize already refuses this — see the F46 guard in
                    // AuthorizeRequestSupport — but the consent deny path bypassed it entirely.
                    if (!string.IsNullOrEmpty(redirectUri)
                        && client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                    {
                        var errorBuilder = new UriBuilder(redirectUri);
                        var errorParams = System.Web.HttpUtility.ParseQueryString(errorBuilder.Query);
                        errorParams["error"] = "access_denied";
                        errorParams["error_description"] = "User denied consent";
                        if (!string.IsNullOrEmpty(state))
                            errorParams["state"] = state;
                        // RFC 9207 iss, as on every other authorization response. This is the one
                        // authorization error the server hand-builds instead of going through
                        // AuthorizeRequestSupport.BuildErrorRedirect, so it was the one that kept
                        // omitting iss after that was fixed — and user-denied-consent is the most
                        // common legitimate error an interactive OP emits. Discovery advertises
                        // authorization_response_iss_parameter_supported unconditionally, so a client
                        // strict enough to require iss (which is the point of asking for it) rejected
                        // a perfectly genuine denial as a suspected mix-up.
                        errorParams["iss"] = tenantContext.Issuer;
                        errorBuilder.Query = errorParams.ToString();
                        return TypedResults.Json(new RedirectResponse { Redirect = errorBuilder.ToString() }, AuthagonalJsonContext.Default.RedirectResponse);
                    }
                }
                return TypedResults.Json(new RedirectResponse { Redirect = "/" }, AuthagonalJsonContext.Default.RedirectResponse);
            }

            // What the user was OFFERED, read from the record the AUTHORIZE endpoint wrote before it
            // sent the user-agent here.
            //
            // It used to be derived from request.ReturnUrl, described as safer than the request body
            // because it "is not the caller's to assert" — but returnUrl is just another field of the
            // same caller-supplied POST, populated from a query parameter on a public SPA route. It is
            // also a DIFFERENT parameter from the `scope` that drove what the screen rendered, so the
            // displayed set and the recorded offered set could be made to diverge by construction.
            // That is worth something to an attacker: AuthorizeEndpoint treats OfferedScopes as
            // "already asked about" and suppresses the consent prompt for anything inside it, so a
            // wide offered set is a way to never be asked about those scopes again.
            //
            // The record is now REQUIRED rather than best-effort. Treating it as optional meant this
            // endpoint would write a real five-year grant for any (subject, client) pair on request,
            // with no authorization request behind it at all — the other half of the crafted-consent-link
            // problem, and the half that persists after the browser is closed.
            var offeredFromServer = await ReadLiveOfferAsync(grantStore, subjectId, request.ClientId, ct);
            if (offeredFromServer is null)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "no_pending_consent_request" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Persist consent — store only scopes that were both OFFERED and ones the client is allowed
            // to request, so a tampered consent body can neither record (and later silently satisfy) a
            // scope beyond the client's AllowedScopes nor one the user was never shown.
            var consentKey = $"consent:{subjectId}:{request.ClientId}";
            var grantedScopes = (request.Scopes ?? [])
                .Where(s => offeredFromServer.Contains(s, StringComparer.Ordinal))
                .Where(s => client.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Intersected with AllowedScopes so a stale record cannot outlive a narrowing of the
            // client's registration, and unioned with the granted set so the two can never disagree.
            var offeredScopes = offeredFromServer
                .Where(s => client.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Union(grantedScopes, StringComparer.Ordinal)
                .ToList();

            // Single-use: the offer belongs to the authorize request that created it.
            await grantStore.RemoveAsync(AuthorizeEndpoint.ConsentOfferKey(subjectId, request.ClientId), ct);

            // ...and record that the screen was shown and answered, so a `prompt=consent` request does not
            // demand it again on the very next pass through the authorize endpoint. Short-lived: it exists
            // only to carry this decision back across the redirect that follows.
            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = AuthorizeEndpoint.ConsentPromptKey(subjectId, request.ClientId),
                Type = PersistedGrantTypes.ConsentPrompt,
                SubjectId = subjectId,
                ClientId = request.ClientId,
                Data = "",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            }, ct);

            var consentData = new AuthorizeEndpoint.ConsentData
            {
                Scopes = grantedScopes,
                OfferedScopes = offeredScopes,
                ConsentedAt = DateTimeOffset.UtcNow,
            };

            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = consentKey,
                Type = "consent",
                SubjectId = subjectId,
                ClientId = request.ClientId,
                Data = JsonSerializer.Serialize(consentData, AuthagonalJsonContext.Default.ConsentData),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(5), // consent doesn't expire quickly
            }, ct);

            // Redirect back to the authorize endpoint to complete the flow. Sanitised: this value was
            // echoed verbatim and the login app assigns it to window.location.href, so an attacker who got
            // a signed-in user to open /login/consent?...&returnUrl=https://evil.example had them redirected
            // off-site from the IdP's own origin on clicking Allow. A `javascript:` URI reached the same
            // sink and was contained only by the CSP, which makes that CSP load-bearing rather than
            // defence-in-depth — so the value is bounded to a same-site path here instead.
            return TypedResults.Json(
                new RedirectResponse { Redirect = Authagonal.Core.Services.LocalRedirect.Sanitize(request.ReturnUrl) },
                AuthagonalJsonContext.Default.RedirectResponse);
        }).RequireAuthorization().RequireOwnOrigin();

        // List all consent grants for the current user
        app.MapGet("/consent/grants", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
            // Same shape as the DELETE below: cookie-authenticated with no .RequireAuthorization(), so the
            // origin guard is applied inline. Reading is not revoking, but the grants list names every
            // application the user has consented to — not something a cross-origin page gets to enumerate.
            if (Services.InteractiveOriginGuard.Check(httpContext) is { } originError)
                return originError;

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(subjectId))
                return Results.Unauthorized();

            var grants = await grantStore.GetBySubjectAsync(subjectId, ct);
            var consentGrants = grants.Where(g => g.Type == "consent").ToList();

            var results = new List<object>();
            foreach (var grant in consentGrants)
            {
                var client = await clientStore.GetAsync(grant.ClientId, ct);
                var data = JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.ConsentData);
                results.Add(new
                {
                    clientId = grant.ClientId,
                    clientName = client?.ClientName ?? grant.ClientId,
                    scopes = data?.Scopes ?? [],
                    consentedAt = data?.ConsentedAt ?? grant.CreatedAt,
                });
            }

            return Results.Ok(results);
        });

        // Revoke an authorized app.
        //
        // This used to remove the consent:{sub}:{clientId} row and nothing else, which meant the button
        // labelled "Revoke access for this app" withdrew only the record of the user's decision. The
        // app's refresh token is a separate grant row that no part of the refresh path consults, so it
        // kept rotating and kept minting access tokens — for up to AbsoluteRefreshTokenLifetimeSeconds
        // (30 days by default) after the user was told access had been revoked. Short of resetting
        // their password there was no way for a user to cut off a misbehaving client at all.
        //
        // So: drop the consent AND the session-bound grants for this client, and revoke the access
        // tokens minted under them, which are self-contained JWTs that outlive their grant otherwise.
        // Scoped to this clientId, so every other authorized app is untouched.
        app.MapDelete("/consent/grants/{clientId}", async (
            string clientId,
            HttpContext httpContext,
            IGrantStore grantStore,
            IEnumerable<IAuthHook> authHooks,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // This route carries no .RequireAuthorization() at all — it authenticates by reading the
            // cookie's subject claim directly below, which is why a fix applied across the
            // RequireAuthorization sites missed it. Revoking a user's consent grants also revokes the access
            // tokens issued under them, so a cross-origin page could sign the victim out of every
            // consenting application. Verified live before this line existed: the cross-origin DELETE
            // returned 204 No Content.
            if (Services.InteractiveOriginGuard.Check(httpContext) is { } originError)
                return originError;

            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(subjectId))
                return Results.Unauthorized();

            // Resolved off the request rather than taken as a handler parameter: an unregistered
            // optional service would otherwise be inferred as a body-bound parameter on a DELETE.
            var revokedTokenStore = httpContext.RequestServices.GetService<IRevokedTokenStore>();

            var removed = await GrantRevocation.RevokeClientGrantsAsync(
                grantStore,
                revokedTokenStore,
                subjectId,
                clientId,
                [.. PersistedGrantTypes.SessionBound, PersistedGrantTypes.Consent],
                loggerFactory.CreateLogger(typeof(ConsentEndpoint)),
                ct);

            await authHooks.RunOnConsentRevokedAsync(subjectId, clientId, removed, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// The scopes the authorize endpoint recorded as offered to this subject for this client, or
    /// <see langword="null"/> when there is no live offer.
    /// </summary>
    /// <remarks>
    /// Both halves of the consent surface hang off this: the screen renders the record's contents, and the
    /// POST refuses outright without one. Expiry is checked here rather than trusted to the store, because
    /// the four providers differ on whether an expired row is still readable.
    /// </remarks>
    private static async Task<string[]?> ReadLiveOfferAsync(
        IGrantStore grantStore, string subjectId, string clientId, CancellationToken ct)
    {
        var record = await grantStore.GetAsync(AuthorizeEndpoint.ConsentOfferKey(subjectId, clientId), ct);
        if (record is null || record.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        return record.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Reads the <c>scope</c> parameter out of the authorize URL the consent screen returns to, which
    /// is the set the screen displayed.
    /// </summary>
    internal sealed class ConsentRequest
    {
        public string ClientId { get; set; } = "";
        public string Decision { get; set; } = "";
        public string[]? Scopes { get; set; }
        public string? ReturnUrl { get; set; }
    }
}
