using Authagonal.Core.Constants;
using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

/// <summary>
/// OIDC Back-Channel Logout 1.0.
/// When a user logs out, notifies all relying parties (clients) that have registered
/// a back-channel logout URI. Sends a signed logout token to each client's URI.
/// </summary>
public static class BackChannelLogoutEndpoint
{
    public static IEndpointRouteBuilder MapBackChannelLogoutEndpoints(this IEndpointRouteBuilder app)
    {
        // Internal endpoint, and nothing in this product calls it. The comment here used to say
        // "called by EndSessionEndpoint after cookie sign-out" — EndSessionEndpoint does not call it;
        // the relying-party fan-out is in-process via SessionTermination. The route exists for a host
        // that builds its own pod-to-pod revocation on top of it, which is also why the guard fails
        // closed without Cluster:Secret: no first-party flow depends on it.
        app.MapPost("/_internal/backchannel-logout", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Internal");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        BackChannelLogoutRequest request,
        HttpContext httpContext,
        IClientStore clientStore,
        IGrantStore grantStore,
        IRevokedTokenStore? revokedTokenStore,
        IKeyManager keyManager,
        ITenantContext tenantContext,
        IHttpClientFactory httpClientFactory,
        IOptions<ClusterOptions> clusterOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Internal-only: this endpoint revokes every grant for a subject. Without a guard, anyone
        // who can reach it could force-logout arbitrary users (session DoS) and probe their sessions.
        if (!InternalEndpointGuard.IsAuthorized(
                httpContext, clusterOptions.Value.Secret, clusterOptions.Value.AllowLoopbackWithoutSecret))
            return Results.NotFound();

        if (string.IsNullOrEmpty(request.SubjectId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "subject_id_required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        // Find all clients with back-channel logout URIs
        // For now, iterate grants to find which clients the user has active sessions with
        var grants = await grantStore.GetBySubjectAsync(request.SubjectId, ct);
        var clientIds = grants
            .Select(g => g.ClientId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var notified = 0;
        var failed = 0;

        foreach (var clientId in clientIds)
        {
            var client = await clientStore.GetAsync(clientId, ct);
            if (client?.BackChannelLogoutUri is null)
                continue;

            // Revalidated at SEND time, not only where the URI is written. Dynamic registration checks
            // it, but the store holds URIs that never went through that path — seeded clients, the Duende
            // migration, a restore, admin writes, an embedding host's own IClientStore, and anything
            // registered before the DCR check existed. This is a server-initiated POST to a caller-chosen
            // target whose response never reaches the caller, so an internal address here is a blind SSRF
            // primitive; checking it at the sink is what makes that true of every URI in the store rather
            // than of the ones one writer happened to police. No loopback exception: the request is made
            // by the server, so loopback is the server's own network namespace.
            //
            // The check is no longer written here as a separate statement. It travels inside
            // SafeOutboundHttp.SendAsync below, so it cannot be left behind by an edit that moves or adds a
            // send — which is what every finding in this area turned out to be. A refusal surfaces as the
            // InvalidOperationException the catch already handles.
            try
            {
                var logoutToken = CreateLogoutToken(
                    tenantContext.Issuer,
                    clientId,
                    request.SubjectId,
                    keyManager);

                var httpClient = httpClientFactory.CreateClient("BackChannelLogout");
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["logout_token"] = logoutToken
                });

                using var logoutRequest = new HttpRequestMessage(
                    HttpMethod.Post, client.BackChannelLogoutUri) { Content = content };
                var response = await Services.SafeOutboundHttp.SendAsync(
                    httpClient, logoutRequest, logger, ct);

                if (response.IsSuccessStatusCode)
                {
                    notified++;
                    logger.LogInformation(
                        "Back-channel logout sent to client {ClientId} for subject {SubjectId}",
                        clientId, request.SubjectId);
                }
                else
                {
                    failed++;
                    logger.LogWarning(
                        "Back-channel logout failed for client {ClientId}: HTTP {StatusCode}",
                        clientId, (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex,
                    "Back-channel logout error for client {ClientId}", clientId);
            }
        }

        // Revoke all grants for the subject, and the access tokens they minted. This endpoint is the
        // cluster's own revocation fan-out — an operator calling it and being told GrantsRevoked=N while N
        // access tokens stayed valid is the reverse of what it exists for.
        await GrantRevocation.RevokeAllSubjectGrantsAsync(
            grantStore, revokedTokenStore, request.SubjectId, logger, ct);

        return TypedResults.Json(new BackChannelLogoutResult { Notified = notified, Failed = failed, GrantsRevoked = grants.Count }, AuthagonalJsonContext.Default.BackChannelLogoutResult);
    }

    private static string CreateLogoutToken(
        string issuer, string clientId, string subjectId, IKeyManager keyManager)
    {
        var signingCredentials = keyManager.GetSigningCredentials();

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            // With Expires unset IdentityModel stamps exp = iat + 60 minutes, so a captured logout token
            // stayed replayable for an hour. Two minutes covers the delivery POST.
            Expires = DateTime.UtcNow.AddMinutes(2),
            // Explicit kind: this token was accepted as a subject_token at /connect/token and exchanged for
            // a live access token.
            TokenType = TokenTypes.LogoutJwt,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subjectId,
                // The event value must be a JSON-serializable empty object — an anonymous type makes
                // JsonWebTokenHandler.CreateToken throw IDX11025, which silently failed every RP notification.
                ["events"] = new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
                },
                ["jti"] = Guid.NewGuid().ToString("N")
            },
            SigningCredentials = signingCredentials
        };

        // Logout tokens MUST NOT contain a nonce
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }
}

public sealed class BackChannelLogoutRequest
{
    public string SubjectId { get; set; } = "";
}
