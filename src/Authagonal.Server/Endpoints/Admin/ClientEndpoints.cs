using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class ClientEndpoints
{
    public static IEndpointRouteBuilder MapClientAdminEndpoints(this IEndpointRouteBuilder app, string policy = "IdentityAdmin")
    {
        var group = app.MapGroup("/api/v1/clients")
            .RequireAuthorization(policy)
            .WithTags("Admin - Clients");

        group.MapGet("/", ListClients);
        group.MapGet("/{clientId}", GetClient);
        group.MapPost("/", CreateClient);
        group.MapPut("/{clientId}", UpdateClient);
        group.MapDelete("/{clientId}", DeleteClient);

        return app;
    }

    private static async Task<IResult> ListClients(IClientStore store, CancellationToken ct)
    {
        var clients = await store.GetAllAsync(ct);
        return TypedResults.Json(clients.ToList(), AuthagonalJsonContext.Default.ListOAuthClient);
    }

    private static async Task<IResult> GetClient(string clientId, IClientStore store, CancellationToken ct)
    {
        var client = await store.GetAsync(clientId, ct);
        return client is null
            ? Results.NotFound()
            : TypedResults.Json(client, AuthagonalJsonContext.Default.OAuthClient);
    }

    private static async Task<IResult> CreateClient(
        OAuthClient client,
        IClientStore store,
        IClientScopeGuard scopeGuard,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientName))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "client_id and client_name are required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (scopeGuard.FindUngrantableScope(http.User, client.AllowedScopes) is not null)
            return Results.Forbid();

        var existing = await store.GetAsync(client.ClientId, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_exists", ErrorDescription = $"Client '{client.ClientId}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        await store.UpsertAsync(client, ct);
        await audit.LogAsync(Actor(http), "client.created", "client", client.ClientId, client.ClientName, ct);
        return Results.Created($"/api/v1/clients/{client.ClientId}", client);
    }

    private static async Task<IResult> UpdateClient(
        string clientId,
        OAuthClient client,
        IClientStore store,
        IClientScopeGuard scopeGuard,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(clientId, ct);
        if (existing is null) return Results.NotFound();

        // Only block escalation on scopes newly added by this update — leaving
        // existing scopes alone is safe even if the caller couldn't grant them today.
        var newlyAdded = client.AllowedScopes.Except(existing.AllowedScopes);
        if (scopeGuard.FindUngrantableScope(http.User, newlyAdded) is not null)
            return Results.Forbid();

        client.ClientId = clientId;
        await store.UpsertAsync(client, ct);
        await audit.LogAsync(Actor(http), "client.updated", "client", clientId, null, ct);
        return TypedResults.Json(client, AuthagonalJsonContext.Default.OAuthClient);
    }

    private static async Task<IResult> DeleteClient(
        string clientId,
        IClientStore store,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(clientId, ct);
        if (existing is null) return Results.NotFound();

        await store.DeleteAsync(clientId, ct);
        await audit.LogAsync(Actor(http), "client.deleted", "client", clientId, null, ct);
        return Results.NoContent();
    }

    private static string Actor(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("email")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue("client_id")
        ?? "unknown";
}

