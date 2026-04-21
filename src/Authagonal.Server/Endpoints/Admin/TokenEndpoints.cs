using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Admin;

public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/token", CreateTokenForUser)
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Tokens");

        return app;
    }

    private static async Task<IResult> CreateTokenForUser(
        HttpContext httpContext,
        IProtocolTokenService tokenService,
        IClientStore clientStore,
        IUserStore userStore,
        UserStoreOidcSubjectResolver subjectResolver,
        CancellationToken ct)
    {
        var query = httpContext.Request.Query;

        var clientId = query["clientId"].FirstOrDefault();
        var userId = query["userId"].FirstOrDefault();
        var scopesParam = query["scopes"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "clientId query parameter is required" });

        if (string.IsNullOrWhiteSpace(userId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "userId query parameter is required" });

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return Results.NotFound(new { error = "client_not_found", error_description = $"Client '{clientId}' not found" });

        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = $"User '{userId}' not found" });

        var scopes = string.IsNullOrWhiteSpace(scopesParam)
            ? client.AllowedScopes
            : scopesParam.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var subject = await subjectResolver.BuildSubjectAsync(user, client, ct: ct);

        var accessToken = await tokenService.CreateAccessTokenAsync(subject, client, scopes, ct: ct);
        var refreshToken = await tokenService.CreateRefreshTokenAsync(subject, client, scopes, ct: ct);

        string? idToken = null;
        if (scopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
        {
            idToken = await tokenService.CreateIdTokenAsync(subject, client, scopes, ct: ct);
        }

        var response = new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            RefreshToken = refreshToken,
            IdToken = idToken,
            Scope = string.Join(' ', scopes)
        };

        return Results.Ok(response);
    }
}
