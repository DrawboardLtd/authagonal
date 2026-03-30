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
            return Results.BadRequest(new { error = "invalid_request", error_description = "clientId is required" });

        var client = await clientStore.GetAsync(request.ClientId, ct);
        if (client is null)
            return Results.NotFound(new { error = "client_not_found", error_description = $"Client '{request.ClientId}' not found" });

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
        return Results.Ok(new
        {
            tokenId = token.TokenId,
            clientId = token.ClientId,
            token = rawToken,
            description = token.Description,
            createdAt = token.CreatedAt,
            expiresAt = token.ExpiresAt,
        });
    }

    private static async Task<IResult> ListTokensAsync(
        string? clientId,
        IScimTokenStore scimTokenStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "clientId query parameter is required" });

        var tokens = await scimTokenStore.GetByClientAsync(clientId, ct);

        var result = tokens.Select(t => new
        {
            tokenId = t.TokenId,
            clientId = t.ClientId,
            description = t.Description,
            createdAt = t.CreatedAt,
            expiresAt = t.ExpiresAt,
            isRevoked = t.IsRevoked,
        });

        return Results.Ok(new { tokens = result });
    }

    private static async Task<IResult> RevokeTokenAsync(
        string tokenId,
        string? clientId,
        IScimTokenStore scimTokenStore,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "clientId query parameter is required" });

        await scimTokenStore.RevokeAsync(tokenId, clientId, ct);

        logger.LogInformation("SCIM token revoked: {TokenId} for client {ClientId}", tokenId, clientId);

        return Results.Ok(new { success = true });
    }
}

public sealed record GenerateScimTokenRequest(string? ClientId, string? Description, int? ExpiresInDays);
