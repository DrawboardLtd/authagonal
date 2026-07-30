using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Configuration;

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
        // Secret hashes must never leave the server — project to copies with them stripped (never
        // mutate the returned instances; some stores hand back the cached objects).
        var list = clients.Select(Redacted).ToList();
        return TypedResults.Json(list, AuthagonalJsonContext.Default.ListOAuthClient);
    }

    private static async Task<IResult> GetClient(string clientId, IClientStore store, CancellationToken ct)
    {
        var client = await store.GetAsync(clientId, ct);
        if (client is null)
            return Results.NotFound();
        return TypedResults.Json(Redacted(client), AuthagonalJsonContext.Default.OAuthClient);
    }

    private static async Task<IResult> CreateClient(
        OAuthClient client,
        IClientStore store,
        IClientScopeGuard scopeGuard,
        IAuditLogger audit,
        IConfiguration configuration,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientName))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "client_id and client_name are required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (scopeGuard.FindUngrantableScope(http.User, client.AllowedScopes) is not null)
            return Results.Forbid();

        // Reserve the admin scope: no client may hold it, otherwise an admin could mint a
        // client_credentials client that issues admin tokens indefinitely (privilege persistence).
        if (IsAdminScopeRequested(client.AllowedScopes, configuration))
            return TypedResults.Json(new ErrorInfoResponse { Error = "forbidden_scope", ErrorDescription = "The administrative scope cannot be granted to a client" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        var existing = await store.GetAsync(client.ClientId, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_exists", ErrorDescription = $"Client '{client.ClientId}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        if (InvalidHomeUri(client) is { } uriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = uriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (client.IsDefaultApplication)
            await ClearOtherDefaultsAsync(store, client.ClientId, ct);
        await store.UpsertAsync(client, ct);
        await audit.LogAsync(Actor(http), "client.created", "client", client.ClientId, client.ClientName, ct);
        return Results.Created($"/api/v1/clients/{client.ClientId}", Redacted(client));
    }

    private static async Task<IResult> UpdateClient(
        string clientId,
        OAuthClient client,
        IClientStore store,
        IClientScopeGuard scopeGuard,
        IAuditLogger audit,
        IConfiguration configuration,
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

        // Reserve the admin scope: a client may never hold it.
        if (IsAdminScopeRequested(client.AllowedScopes, configuration))
            return TypedResults.Json(new ErrorInfoResponse { Error = "forbidden_scope", ErrorDescription = "The administrative scope cannot be granted to a client" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        client.ClientId = clientId;
        // Preserve the stored secret when the update omits hashes; never echo them back. (A rotation
        // that explicitly supplies new hashes is still honoured.)
        if (client.ClientSecretHashes is not { Count: > 0 })
            client.ClientSecretHashes = existing.ClientSecretHashes;
        if (InvalidHomeUri(client) is { } uriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = uriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (client.IsDefaultApplication && !existing.IsDefaultApplication)
            await ClearOtherDefaultsAsync(store, clientId, ct);
        await store.UpsertAsync(client, ct);
        await audit.LogAsync(Actor(http), "client.updated", "client", clientId, null, ct);
        return TypedResults.Json(Redacted(client), AuthagonalJsonContext.Default.OAuthClient);
    }

    // Home URIs are rendered as navigation targets on the hosted account pages, so they must be
    // absolute https (http only for loopback, for local dev). Rejecting here keeps javascript:/data:
    // and scheme-relative values out of the store entirely.
    private static string? InvalidHomeUri(OAuthClient client)
    {
        foreach (var (name, value) in new[] { ("client_uri", client.ClientUri), ("initiate_login_uri", client.InitiateLoginUri) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                return $"{name} must be an absolute https URL (http is allowed for loopback only)";
        }
        return null;
    }

    // At most one client may be the default application: taking the flag clears it elsewhere.
    // Copies via `with` — stores may hand back cached instances that must not be mutated.
    private static async Task ClearOtherDefaultsAsync(IClientStore store, string keepClientId, CancellationToken ct)
    {
        var all = await store.GetAllAsync(ct);
        foreach (var other in all.Where(c => c.IsDefaultApplication && c.ClientId != keepClientId))
            await store.UpsertAsync(other with { IsDefaultApplication = false }, ct);
    }

    private static bool IsAdminScopeRequested(IEnumerable<string> scopes, IConfiguration configuration)
    {
        var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;
        // Splits each entry on whitespace: a whole-string comparison treated "authagonal-admin x" as an
        // unrelated scope while every consumer that splits saw the admin scope inside it.
        return AdminScopeReservation.Grants(scopes, adminScope);
    }

    // Copy of a client with secret hashes stripped, for safe return to admins. Never mutate the
    // passed-in instance — stores may hand back (and retain) the cached object.
    private static OAuthClient Redacted(OAuthClient c) => c with { ClientSecretHashes = [] };

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

