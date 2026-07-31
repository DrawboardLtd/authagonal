using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class ScimTokenEndpoints
{
    public static IEndpointRouteBuilder MapScimTokenAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scim/tokens")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - SCIM Tokens");

        group.MapPost("/", GenerateTokenAsync).DisableAntiforgery();
        group.MapGet("/", ListTokensAsync);
        group.MapDelete("/{tokenId}", RevokeTokenAsync);

        return app;
    }

    private static async Task<IResult> GenerateTokenAsync(
        GenerateScimTokenRequest request,
        IScimTokenStore scimTokenStore,
        IClientStore clientStore,
        IAuditLogger audit,
        HttpContext http,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var client = await clientStore.GetAsync(request.ClientId, ct);
        if (client is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found", ErrorDescription = $"Client '{request.ClientId}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        // Generate a cryptographically secure token
        var rawTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawTokenBytes);

        // Hash for storage
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

        var token = new ScimToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            ClientId = request.ClientId,
            TokenHash = tokenHash,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresInDays > 0
                ? DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays.Value)
                : null,
        };

        await scimTokenStore.StoreAsync(token, ct);

        // A SCIM token is a directory-wide read/write credential, shown once and never recoverable. It was
        // recorded only as an unstructured log line with no actor, so the audit trail — which faithfully
        // records who renamed a client — held nothing about who minted the credential that can rewrite
        // every user in the tenant.
        await audit.LogAsync(AdminActor.Of(http), "scim_token.created", "scim_token", token.TokenId, token.ClientId, ct);

        logger.LogInformation("SCIM token generated: {TokenId} for client {ClientId}", token.TokenId, token.ClientId);

        // Return the raw token once — it cannot be recovered later
        return TypedResults.Json(new ScimTokenCreatedResponse
        {
            TokenId = token.TokenId,
            ClientId = token.ClientId,
            Token = rawToken,
            Description = token.Description,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt ?? default,
        }, AuthagonalJsonContext.Default.ScimTokenCreatedResponse);
    }

    private static async Task<IResult> ListTokensAsync(
        string? clientId,
        IScimTokenStore scimTokenStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId query parameter is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var tokens = await scimTokenStore.GetByClientAsync(clientId, ct);

        var result = tokens.Select(t => new ScimTokenInfo
        {
            TokenId = t.TokenId,
            ClientId = t.ClientId,
            Description = t.Description,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt ?? default,
            IsRevoked = t.IsRevoked,
        });

        return TypedResults.Json(new ScimTokenListResponse { Tokens = result }, AuthagonalJsonContext.Default.ScimTokenListResponse);
    }

    private static async Task<IResult> RevokeTokenAsync(
        string tokenId,
        string? clientId,
        IScimTokenStore scimTokenStore,
        IAuditLogger audit,
        HttpContext http,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId query parameter is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        await scimTokenStore.RevokeAsync(tokenId, clientId, ct);

        // Revocation is the other half: cutting a connector's access is exactly the change an operator
        // later has to prove was deliberate rather than an attacker disabling provisioning.
        await audit.LogAsync(AdminActor.Of(http), "scim_token.revoked", "scim_token", tokenId, clientId, ct);

        logger.LogInformation("SCIM token revoked: {TokenId} for client {ClientId}", tokenId, clientId);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }
}

public sealed class GenerateScimTokenRequest
{
    public string? ClientId { get; set; }
    public string? Description { get; set; }
    public int? ExpiresInDays { get; set; }
}
