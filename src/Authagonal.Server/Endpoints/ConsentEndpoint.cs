using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;

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

                    if (!string.IsNullOrEmpty(redirectUri))
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

            // Redirect back to authorize endpoint to complete the flow
            return TypedResults.Json(new RedirectResponse { Redirect = request.ReturnUrl ?? "/" }, AuthagonalJsonContext.Default.RedirectResponse);
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

        // Revoke consent for a specific client
        app.MapDelete("/consent/grants/{clientId}", async (
            string clientId,
            HttpContext httpContext,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
            var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(subjectId))
                return Results.Unauthorized();

            var consentKey = $"consent:{subjectId}:{clientId}";
            await grantStore.RemoveAsync(consentKey, ct);
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
