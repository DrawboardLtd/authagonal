using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;

namespace Authagonal.Server.Endpoints;

public static class ConsentEndpoint
{
    public static IEndpointRouteBuilder MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/consent/info", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IScopeStore scopeStore,
            string client_id,
            string? scope,
            CancellationToken ct) =>
        {
            var client = await clientStore.GetAsync(client_id, ct);
            if (client is null)
                return (IResult)TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            var requestedScopes = (scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
        });

        app.MapPost("/consent", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
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
                        errorBuilder.Query = errorParams.ToString();
                        return TypedResults.Json(new RedirectResponse { Redirect = errorBuilder.ToString() }, AuthagonalJsonContext.Default.RedirectResponse);
                    }
                }
                return TypedResults.Json(new RedirectResponse { Redirect = "/" }, AuthagonalJsonContext.Default.RedirectResponse);
            }

            // Persist consent — store only scopes the client is actually allowed to request, so a
            // tampered consent body can't record (and later silently satisfy) scopes beyond the
            // client's AllowedScopes.
            var consentKey = $"consent:{subjectId}:{request.ClientId}";
            var grantedScopes = (request.Scopes ?? [])
                .Where(s => client.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // What the user was OFFERED, read out of the authorize URL we are about to return to
            // rather than taken from the request body. The body's job is to say what was approved; a
            // wider offered set suppresses future prompts, so it is not the caller's to assert.
            // Unioned with the granted set so a PAR authorize URL — which carries no `scope` — still
            // records at least what was approved.
            var offeredScopes = OfferedScopesFromReturnUrl(request.ReturnUrl)
                .Where(s => client.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Union(grantedScopes, StringComparer.Ordinal)
                .ToList();

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
        }).RequireAuthorization();

        // List all consent grants for the current user
        app.MapGet("/consent/grants", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
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
    /// Reads the <c>scope</c> parameter out of the authorize URL the consent screen returns to, which
    /// is the set the screen displayed.
    /// </summary>
    private static IEnumerable<string> OfferedScopesFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return [];

        // returnUrl is a path+query, so it needs a base to parse against. The host is discarded —
        // only the query is read.
        if (!Uri.TryCreate(new Uri("https://placeholder"), returnUrl, out var uri))
            return [];

        return (System.Web.HttpUtility.ParseQueryString(uri.Query)["scope"] ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    internal sealed class ConsentRequest
    {
        public string ClientId { get; set; } = "";
        public string Decision { get; set; } = "";
        public string[]? Scopes { get; set; }
        public string? ReturnUrl { get; set; }
    }
}
