using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
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
        // Internal endpoint called by EndSessionEndpoint after cookie sign-out
        app.MapPost("/_internal/backchannel-logout", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Internal");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        BackChannelLogoutRequest request,
        IClientStore clientStore,
        IGrantStore grantStore,
        IKeyManager keyManager,
        ITenantContext tenantContext,
        IHttpClientFactory httpClientFactory,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SubjectId))
            return Results.BadRequest(new { error = "subject_id_required" });

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

                var response = await httpClient.PostAsync(client.BackChannelLogoutUri, content, ct);

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

        // Revoke all grants for the subject
        await grantStore.RemoveAllBySubjectAsync(request.SubjectId, ct);

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
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subjectId,
                ["events"] = new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] = new { }
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
