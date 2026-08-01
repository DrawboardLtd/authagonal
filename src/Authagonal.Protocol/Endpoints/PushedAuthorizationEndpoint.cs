using System.Text.Json;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
            // where only invalid_client is), and every one of them carries the RFC 6749 §5.2
            // challenge — the token endpoint's fix never reached this sibling.
            var (client, authError) = await ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier,
                (error, description) => JsonResults.UnauthorizedClient(error, description, realm: "par"), ct);
            if (authError is not null)
                return authError;

            var clientId = client!.ClientId;

            // Throttled per client, because a PAR request is an anonymous write.
            //
            // A public client authenticates here on a bare client_id, which is readable from any SPA's
            // network traffic, and every accepted request persists a grant row keyed by a fresh
            // request_uri. Unthrottled that is a storage-flood primitive against the grant store from
            // an unauthenticated caller — the same exposure /connect/register was rate-limited for.
            // The budget is deliberately generous: it bounds a flood, it must not interfere with a
            // busy client's normal traffic. Resolved through the service provider because IRateLimiter
            // is a host registration this package does not make (a Server host has one; an embedding
            // host may bring a distributed one) — the same optional seam the client-secret throttle in
            // ClientAuthentication uses.
            var limiter = httpContext.RequestServices.GetService<IRateLimiter>();
            if (limiter is not null &&
                await limiter.IsRateLimitedAsync($"par|{clientId}", 300, TimeSpan.FromMinutes(1), ct))
            {
                return JsonResults.OAuthError("temporarily_unavailable", "Too many pushed authorization requests", 429);
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

            // Validated NOW, not only when the request_uri is later redeemed.
            //
            // RFC 9126 §2.1 step 3 requires the AS to validate a pushed request as it would the same
            // parameters on the authorization endpoint. This stored whatever was posted, so a request
            // with an unregistered redirect_uri, an unknown scope or a missing PKCE challenge was
            // accepted with a 201 and a request_uri — and failed only at /connect/authorize, by which
            // point the error surfaces to the END USER mid-flow instead of to the client that made
            // the mistake. Validating here also means an invalid request never occupies a stored row.
            var pushed = AuthorizeRequest.Read(new ParRequestParameters(parameters));

            // Checked before the generic pass so the client gets RFC 9396 §5's error code rather than
            // this endpoint's catch-all invalid_request. A pushed request is the one place where a
            // wrong code is especially costly: the client fixes what it can see.
            if (!string.IsNullOrWhiteSpace(pushed.AuthorizationDetails))
                return JsonResults.OAuthError("invalid_authorization_details",
                    "authorization_details is not accepted at the authorization endpoint; "
                    + "request rich authorization details on the token endpoint (RFC 8693 exchange)");

            if (AuthorizeRequestSupport.Validate(client, pushed) is not null)
            {
                return JsonResults.OAuthError(
                    "invalid_request",
                    "the pushed authorization request is not valid for this client");
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
        .RequireTls()
        .WithTags("OAuth");

        return app;
    }
}
