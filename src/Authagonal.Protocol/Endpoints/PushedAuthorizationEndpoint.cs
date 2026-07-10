using System.Text.Json;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// RFC 9126 Pushed Authorization Requests. The client POSTs the authorize-request parameters
/// with standard client auth and receives a short-lived opaque request_uri to hand to the
/// browser, keeping the parameters off the URL bar and integrity-checked.
/// </summary>
internal static class PushedAuthorizationEndpoint
{
    public static IEndpointRouteBuilder MapProtocolPushedAuthorizationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/par", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IClientSecretVerifier secretVerifier,
            ProtocolPushedAuthorizationService parService,
            CancellationToken ct) =>
        {
            if (!httpContext.Request.HasFormContentType)
                return JsonResults.OAuthError("invalid_request", "application/x-www-form-urlencoded required");

            var form = await httpContext.Request.ReadFormAsync(ct);

            // RFC 9126 client-auth failures are all 401 here (unlike the token endpoint,
            // where only invalid_client is).
            var (client, authError) = await ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier,
                (error, description) => JsonResults.OAuthError(error, description, statusCode: 401), ct);
            if (authError is not null)
                return authError;

            var clientId = client!.ClientId;

            // RFC 9126 §2.1: request_uri MUST NOT be sent to PAR — chaining is forbidden.
            if (form.ContainsKey("request_uri"))
                return JsonResults.OAuthError("invalid_request", "request_uri is not permitted in a pushed request");

            // A submitted client_id MUST match the authenticated client.
            var bodyClientId = form["client_id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(bodyClientId) && !string.Equals(bodyClientId, clientId, StringComparison.Ordinal))
                return JsonResults.OAuthError("invalid_request", "client_id mismatch");

            // Copy form values sans client credentials — the server already authenticated.
            var parameters = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var field in form)
            {
                if (field.Key is "client_id" or "client_secret")
                    continue;
                parameters[field.Key] = field.Value.Where(v => v is not null).Cast<string>().ToArray();
            }

            var response = await parService.StoreAsync(clientId, parameters, ct);

            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            httpContext.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                response,
                ProtocolJsonContext.Default.PushedAuthorizationResponse,
                ct);
            return Results.Empty;
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        return app;
    }
}
