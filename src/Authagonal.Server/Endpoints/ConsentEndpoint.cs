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
                return (IResult)TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            var requestedScopes = (scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return (IResult)TypedResults.Json(new ConsentInfoResponse
            {
                ClientId = client.ClientId,
                ClientName = client.ClientName,
                Description = client.Description,
                ClientUri = client.ClientUri,
                LogoUri = client.LogoUri,
                Scopes = requestedScopes,
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
                Data = JsonSerializer.Serialize(consentData, AuthagonalJsonContext.Default.ConsentData),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(5), // consent doesn't expire quickly
            }, ct);

            // Redirect back to authorize endpoint to complete the flow
            return TypedResults.Json(new RedirectResponse { Redirect = request.ReturnUrl ?? "/" }, AuthagonalJsonContext.Default.RedirectResponse);
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

    internal sealed class ConsentRequest
    {
        public string ClientId { get; set; } = "";
        public string Decision { get; set; } = "";
        public string[]? Scopes { get; set; }
        public string? ReturnUrl { get; set; }
    }
}
