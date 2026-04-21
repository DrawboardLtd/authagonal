using System.Text;
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
public static class PushedAuthorizationEndpoint
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

            var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

            if (string.IsNullOrWhiteSpace(clientId))
                return JsonResults.OAuthError("invalid_client", "client_id is required", statusCode: 401);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return JsonResults.OAuthError("invalid_client", "Unknown client", statusCode: 401);

            if (!client.Enabled)
                return JsonResults.OAuthError("unauthorized_client", "Client is disabled", statusCode: 401);

            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return JsonResults.OAuthError("invalid_client", "client_secret is required", statusCode: 401);
                if (!await secretVerifier.VerifyAsync(client, clientSecret, ct))
                    return JsonResults.OAuthError("invalid_client", "Invalid client credentials", statusCode: 401);
            }

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

    private static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..];
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    var id = Uri.UnescapeDataString(decoded[..colonIndex]);
                    var secret = Uri.UnescapeDataString(decoded[(colonIndex + 1)..]);
                    return (id, secret);
                }
            }
            catch (FormatException) { /* fall through */ }
        }

        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }
}
