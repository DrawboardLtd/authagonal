using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
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

        if (InvalidSecretHashes(client) is { } createHashError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = createHashError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        // 403 with a reason, not Results.Forbid().
        //
        // Forbid() runs the authentication scheme's forbid handler, and on a cookie scheme that is a
        // 302 to /login — so an admin API client that had authenticated perfectly well was answered
        // with a login page for an authorization failure. It could not tell "your token expired" from
        // "you may not grant that scope", and an automated caller followed the redirect and parsed
        // HTML as its API response.
        if (scopeGuard.FindUngrantableScope(http.User, client.AllowedScopes) is { } ungrantable)
            return TypedResults.Json(
                new ErrorInfoResponse
                {
                    Error = "forbidden_scope",
                    ErrorDescription = $"You may not grant the scope '{ungrantable}' to a client.",
                },
                AuthagonalJsonContext.Default.ErrorInfoResponse,
                statusCode: 403);

        // Reserve the admin scope: no client may hold it, otherwise an admin could mint a
        // client_credentials client that issues admin tokens indefinitely (privilege persistence).
        if (IsAdminScopeRequested(client.AllowedScopes, configuration))
            return TypedResults.Json(new ErrorInfoResponse { Error = "forbidden_scope", ErrorDescription = "The administrative scope cannot be granted to a client" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        var existing = await store.GetAsync(client.ClientId, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_exists", ErrorDescription = $"Client '{client.ClientId}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        if (InvalidRedirectUris(client) is { } redirectError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = redirectError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
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
        // Same 403-with-a-reason as CreateClient, and for the same reason: Results.Forbid() answers a
        // JSON API caller with the cookie scheme's 302 to /login. This path kept the Forbid() when the
        // create path was fixed, and nothing caught it because the shipped AllowAll guard never
        // reaches either branch — see ClientScopeGuardDenialTests.
        if (scopeGuard.FindUngrantableScope(http.User, newlyAdded) is { } ungrantable)
            return TypedResults.Json(
                new ErrorInfoResponse
                {
                    Error = "forbidden_scope",
                    ErrorDescription = $"You may not grant the scope '{ungrantable}' to a client.",
                },
                AuthagonalJsonContext.Default.ErrorInfoResponse,
                statusCode: 403);

        // Reserve the admin scope: a client may never hold it.
        if (IsAdminScopeRequested(client.AllowedScopes, configuration))
            return TypedResults.Json(new ErrorInfoResponse { Error = "forbidden_scope", ErrorDescription = "The administrative scope cannot be granted to a client" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        if (InvalidSecretHashes(client) is { } hashError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = hashError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        client.ClientId = clientId;
        // Preserve the stored secret when the update omits hashes; never echo them back. (A rotation
        // that explicitly supplies new hashes is still honoured.)
        if (client.ClientSecretHashes is not { Count: > 0 })
            client.ClientSecretHashes = existing.ClientSecretHashes;
        if (InvalidRedirectUris(client) is { } redirectError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = redirectError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (InvalidHomeUri(client) is { } uriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = uriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (client.IsDefaultApplication && !existing.IsDefaultApplication)
            await ClearOtherDefaultsAsync(store, clientId, ct);
        await store.UpsertAsync(client, ct);
        await audit.LogAsync(Actor(http), "client.updated", "client", clientId, null, ct);
        return TypedResults.Json(Redacted(client), AuthagonalJsonContext.Default.OAuthClient);
    }

    /// <summary>
    /// Rejects a caller-supplied <c>clientSecretHashes</c> entry that this server would not itself
    /// have written, returning the reason. Null when every entry is acceptable.
    /// </summary>
    /// <remarks>
    /// The admin API binds the whole <see cref="OAuthClient"/> from the request body and honoured
    /// <c>ClientSecretHashes</c> verbatim, with nothing validating the format, length or parameters.
    /// A hash is an instruction to this server about how much CPU to spend on the next anonymous
    /// <c>/connect/token</c> call for that client, so an unvalidated one is a remote CPU-exhaustion
    /// primitive that any IdentityAdmin — or a stolen admin token — could plant and then trigger
    /// anonymously. The parser is now bounded too, but refusing unrecognised blobs here is the half
    /// that keeps the decision on formats this server actually produces.
    /// <para>
    /// An empty or whitespace entry is refused for a separate reason: <c>VerifyPassword</c> throws
    /// <see cref="ArgumentException"/> on one, which turned a <c>[""]</c> entry into an unhandled 500
    /// on every token request for that client.
    /// </para>
    /// </remarks>
    private static string? InvalidSecretHashes(OAuthClient client)
    {
        if (client.ClientSecretHashes is not { Count: > 0 } hashes)
            return null;

        foreach (var hash in hashes)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return "clientSecretHashes must not contain empty entries";

            if (!PasswordHasher.IsRecognisedHashFormat(hash))
            {
                return "clientSecretHashes entries must be hashes produced by this server " +
                       "(PBKDF2v2$…) or a supported migration format (PBKDF2v1$…, SHA256$…, SHA512$…, bcrypt)";
            }
        }

        return null;
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
    /// <summary>
    /// The same redirect-URI rules the dynamic-registration endpoint applies.
    /// </summary>
    /// <remarks>
    /// This path validated only ClientUri and InitiateLoginUri; RedirectUris and
    /// PostLogoutRedirectUris went to the store untouched. So the two registration surfaces disagreed
    /// about what a valid redirect URI is, and it was the privileged one that would accept
    /// `javascript:`, a fragment, or cleartext http to an arbitrary host — the last of which puts an
    /// authorization code on a link any on-path party can read.
    /// </remarks>
    private static string? InvalidRedirectUris(OAuthClient client) =>
        RedirectUriRules.Validate(client.RedirectUris, "redirect_uris", requireHttps: true)
        ?? RedirectUriRules.Validate(client.PostLogoutRedirectUris, "post_logout_redirect_uris", requireHttps: false);

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

