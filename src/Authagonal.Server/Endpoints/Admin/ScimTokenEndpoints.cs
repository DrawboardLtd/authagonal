using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
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
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId query parameter is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        await scimTokenStore.RevokeAsync(tokenId, clientId, ct);

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
