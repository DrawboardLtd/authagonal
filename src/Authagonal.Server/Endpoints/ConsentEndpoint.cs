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
            string client_id,
            string? scope,
            CancellationToken ct) =>
        {
            var client = await clientStore.GetAsync(client_id, ct);
            if (client is null)
                return Results.NotFound(new { error = "client_not_found" });

            var requestedScopes = (scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return Results.Ok(new
            {
                clientId = client.ClientId,
                clientName = client.ClientName,
                scopes = requestedScopes,
            });
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
                return Results.NotFound(new { error = "client_not_found" });

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
                        return Results.Ok(new { redirect = errorBuilder.ToString() });
                    }
                }
                return Results.Ok(new { redirect = "/" });
            }

            // Persist consent
            var consentKey = $"consent:{subjectId}:{request.ClientId}";
            var consentData = new AuthorizeEndpoint.ConsentData
            {
                Scopes = request.Scopes?.ToList() ?? [],
                ConsentedAt = DateTimeOffset.UtcNow,
            };

            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = consentKey,
                Type = "consent",
                SubjectId = subjectId,
                ClientId = request.ClientId,
                Data = JsonSerializer.Serialize(consentData),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(5), // consent doesn't expire quickly
            }, ct);

            // Redirect back to authorize endpoint to complete the flow
            return Results.Ok(new { redirect = request.ReturnUrl ?? "/" });
        });

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
                var data = JsonSerializer.Deserialize<AuthorizeEndpoint.ConsentData>(grant.Data);
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

    private sealed record ConsentRequest(string ClientId, string Decision, string[]? Scopes, string? ReturnUrl);
}
