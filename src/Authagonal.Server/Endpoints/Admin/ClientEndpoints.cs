using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        if (MalformedScope(client.AllowedScopes) is { } createMalformed)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = createMalformed }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var existing = await store.GetAsync(client.ClientId, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_exists", ErrorDescription = $"Client '{client.ClientId}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        if (InvalidRedirectUris(client) is { } redirectError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = redirectError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (InvalidHomeUri(client) is { } uriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = uriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (InvalidLogoutUris(client) is { } logoutUriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = logoutUriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (InvalidPublicClientGrants(client) is { } grantError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = grantError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (Authagonal.Core.Services.ResourceAudiencePolicy.RejectAudiences(client.Audiences) is { } createAudienceError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = createAudienceError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        // The admin sent the whole client record, audiences included, so its answer counts — including an
        // empty list, which now means "may not name a resource" rather than "was never asked". Forced
        // server-side rather than taken from the body: a caller cannot opt a new client back into the
        // legacy permissive reading.
        client.AudiencesDeclared = true;

        if (client.IsDefaultApplication)
            await ClearOtherDefaultsAsync(store, client.ClientId, ct);
        await store.UpsertAsync(client, ct);
        await InvalidateCorsAsync(http, ct);
        await audit.LogAsync(Actor(http), "client.created", "client", client.ClientId, client.ClientName, ct);
        return Results.Created($"/api/v1/clients/{client.ClientId}", Redacted(client));
    }

    /// <summary>
    /// Applies only the properties the request body actually carries, over the stored record.
    /// </summary>
    /// <remarks>
    /// The handler used to bind a whole <see cref="OAuthClient"/> and write it, so every JSON property the body
    /// omitted arrived at its declared default and was persisted. Two fields were special-cased —
    /// <c>ClientSecretHashes</c> and <c>AudiencesDeclared</c>, the latter with a comment explaining precisely
    /// this hazard — and about forty were not. A PUT that meant to change one field therefore reset
    /// <c>Enabled</c> to <b>true</b>, <c>MfaPolicy</c> to Disabled, <c>RequireConsent</c> and
    /// <c>RequirePushedAuthorizationRequests</c> to false, <c>JwksJson</c>/<c>JwksUri</c> to null (killing
    /// private_key_jwt), and every URI/scope/origin list to empty.
    /// <para>
    /// The operational consequence needs no attacker: an operator disables a compromised client, and the next
    /// two-field maintenance PUT — a rename, a logo URL, an admin console posting only the fields it renders, a
    /// scripted bulk update — silently re-enables it and drops its MFA requirement. Nothing in the response or
    /// the audit row said so.
    /// </para>
    /// <para>
    /// Absent and explicitly-default are indistinguishable once JSON has been bound to a non-nullable record,
    /// so the fix has to happen before binding. Merging at the node level keeps every existing validation
    /// working on a complete <see cref="OAuthClient"/> and needs no per-property list to fall out of date — the
    /// failure mode of a hand-written merge being exactly the one this replaces.
    /// </para>
    /// <para>
    /// This makes PUT a merge rather than a replace. That is the same choice <c>ClientSeedService</c> makes
    /// ("a field the seed omits preserves the stored value") and for the same reason: on this resource the
    /// omitted-field default is a security downgrade, and no caller has ever been able to rely on the reset
    /// behaviour deliberately.
    /// </para>
    /// </remarks>
    private static OAuthClient MergeOntoStored(OAuthClient existing, JsonElement body)
    {
        var merged = JsonSerializer.SerializeToNode(existing, AuthagonalJsonContext.Default.OAuthClient)!.AsObject();

        foreach (var supplied in body.EnumerateObject())
        {
            // Drop any key that differs only by case before setting, so a caller using PascalCase does not
            // leave the serialized camelCase key behind for the deserializer to pick between.
            foreach (var existingKey in merged.Select(p => p.Key)
                         .Where(k => string.Equals(k, supplied.Name, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                merged.Remove(existingKey);
            }

            merged[supplied.Name] = JsonNode.Parse(supplied.Value.GetRawText());
        }

        return merged.Deserialize(AuthagonalJsonContext.Default.OAuthClient)!;
    }

    private static async Task<IResult> UpdateClient(
        string clientId,
        JsonElement body,
        IClientStore store,
        IClientScopeGuard scopeGuard,
        IAuditLogger audit,
        IConfiguration configuration,
        HttpContext http,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(clientId, ct);
        if (existing is null) return Results.NotFound();

        if (body.ValueKind != JsonValueKind.Object)
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "A JSON object body is required." },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var client = MergeOntoStored(existing, body);

        // Which properties the caller actually sent. The merge means `client` now carries STORED values for
        // everything else, and running the format validators over those would refuse an update because of a
        // field the caller never touched — a client whose stored hash or redirect URI predates the validator
        // that now rejects it could not be edited at all, and the error would name a field the operator did not
        // send. So each validator below is gated on its own input having been supplied.
        var supplied = new HashSet<string>(
            body.EnumerateObject().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        bool Sent(params string[] names) => names.Any(supplied.Contains);

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

        if (Sent("allowedScopes", "scopes") && MalformedScope(client.AllowedScopes) is { } updateMalformed)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = updateMalformed }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (Sent("clientSecretHashes", "secretHashes") && InvalidSecretHashes(client) is { } hashError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = hashError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        if (Sent("audiences") && Authagonal.Core.Services.ResourceAudiencePolicy.RejectAudiences(client.Audiences) is { } audienceError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = audienceError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        // Monotonic even against an EXPLICIT false: an update may set the declaration but never clear it.
        // Omission is already handled by the merge; this is the stronger rule, because unsetting only ever
        // returns the client to the permissive reading of an empty audience list.
        client.AudiencesDeclared = existing.AudiencesDeclared || client.AudiencesDeclared;

        client.ClientId = clientId;
        // Belt and braces on top of the merge: an explicitly empty hash list is not a way to erase the stored
        // secret. (A rotation that supplies new hashes is still honoured.) Hashes are never echoed back.
        if (client.ClientSecretHashes is not { Count: > 0 })
            client.ClientSecretHashes = existing.ClientSecretHashes;
        if (Sent("redirectUris", "postLogoutRedirectUris") && InvalidRedirectUris(client) is { } redirectError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = redirectError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (Sent("clientUri", "initiateLoginUri") && InvalidHomeUri(client) is { } uriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = uriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (Sent("backChannelLogoutUri", "frontChannelLogoutUri") && InvalidLogoutUris(client) is { } logoutUriError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = logoutUriError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (Sent("allowedGrantTypes", "grantTypes", "requireClientSecret") && InvalidPublicClientGrants(client) is { } grantError)
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = grantError }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        if (client.IsDefaultApplication && !existing.IsDefaultApplication)
            await ClearOtherDefaultsAsync(store, clientId, ct);
        await store.UpsertAsync(client, ct);
        await InvalidateCorsAsync(http, ct);
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
    /// <summary>
    /// How many secret hashes one client may carry.
    /// </summary>
    /// <remarks>
    /// The format of each entry was bounded and the COUNT was not, while the verifier loops the whole list
    /// running a full derivation per entry with no early exit on a wrong secret. Total work per request is
    /// therefore <c>count × per-hash cost</c> with <c>count</c> caller-chosen: 1,000 entries at the permitted
    /// 1,000,000 PBKDF2 iterations each is minutes of uncancellable, thread-pinning CPU per anonymous
    /// <c>/connect/token</c> call, and the throttle still allows 30 of those per minute per client.
    /// <para>
    /// Eight is well past what rotation needs (the outgoing secret plus the incoming one, with room for a
    /// staged third).
    /// </para>
    /// </remarks>
    private const int MaxSecretHashesPerClient = 8;

    private static string? InvalidSecretHashes(OAuthClient client)
    {
        if (client.ClientSecretHashes is not { Count: > 0 } hashes)
            return null;

        if (hashes.Count > MaxSecretHashesPerClient)
            return $"clientSecretHashes must contain at most {MaxSecretHashesPerClient} entries: every entry " +
                   "is derived on each failed authentication, so the list length is itself a CPU cost paid on " +
                   "anonymous token requests";

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

    /// <summary>
    /// The same outbound-URL rule the dynamic-registration endpoint applies to the two logout URIs.
    /// </summary>
    /// <remarks>
    /// Both are dereferenced by the SERVER — back-channel is an outbound POST from the logout path,
    /// front-channel is rendered into an iframe — so an unvalidated value is server-side SSRF with an
    /// attacker-chosen target. DCR was hardened and this path was not, which left the privileged
    /// surface as the permissive one: an IdentityAdmin, or a stolen admin token, could point a client
    /// at <c>http://169.254.169.254/…</c> and have the logout path fetch it on demand.
    /// </remarks>
    private static string? InvalidLogoutUris(OAuthClient client)
    {
        foreach (var (name, value) in new[]
                 {
                     ("backChannelLogoutUri", client.BackChannelLogoutUri),
                     ("frontChannelLogoutUri", client.FrontChannelLogoutUri),
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!OutboundUrl.IsSafe(value))
                return $"{name} must be an absolute http(s) URL to an external host";
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

    /// <summary>
    /// Why an entry is not a single scope token, or null when every entry is well-formed.
    /// </summary>
    /// <remarks>
    /// The reservation above and <see cref="IClientScopeGuard.FindUngrantableScope"/> both compare whole
    /// LIST ELEMENTS, while the emitted <c>scope</c> claim is space-delimited and every consumer splits
    /// it. So a single stored entry containing whitespace evades both guards and then expands into
    /// several scopes on the wire — the admin case is the sharpest, but a host with a restrictive
    /// IClientScopeGuard was evadable exactly the same way for any scope it withholds.
    /// </remarks>
    private static string? MalformedScope(IEnumerable<string> scopes) =>
        AdminScopeReservation.FindMalformedScope(scopes) is { } bad
            ? $"Scope entry '{bad}' is not a single scope token. Scope names cannot contain whitespace — list each scope separately."
            : null;

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

    /// <summary>
    /// RFC 6749 §4.4 restricts <c>client_credentials</c> to confidential clients, so a client holding
    /// that grant with no secret requirement is a client that can never use it.
    /// </summary>
    /// <remarks>
    /// The token endpoint already refuses the combination at runtime. Refusing it at write time as well
    /// is the difference between a deployment that fails on its first machine-to-machine call and one
    /// that cannot be configured into that state at all — an operator who sets it has misunderstood
    /// something, and the useful moment to say so is while they are looking at the client.
    /// </remarks>
    private static string? InvalidPublicClientGrants(OAuthClient client) =>
        !client.RequireClientSecret
        && client.AllowedGrantTypes.Contains(Authagonal.Core.Constants.GrantTypes.ClientCredentials, StringComparer.Ordinal)
            ? "client_credentials requires a confidential client: set require_client_secret, or remove "
              + "client_credentials from grant_types. The token endpoint refuses this combination at runtime."
            : null;

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
        await InvalidateCorsAsync(http, ct);
        await audit.LogAsync(Actor(http), "client.deleted", "client", clientId, null, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Drops every node's cached CORS origins for this tenant after a client write.
    /// </summary>
    /// <remarks>
    /// The credentialed origin list is pooled from the client table and cached for
    /// <c>Cache:CorsCacheMinutes</c> — 60 by default — with no invalidation at all. So disabling a
    /// compromised client, or removing an origin from one, left that origin able to make credentialed
    /// cross-origin calls to the protocol surface for up to an hour on every node with a warm entry.
    /// Best-effort by design: a bus failure must not fail the write the admin actually asked for, and
    /// the entry still expires on its own.
    /// </remarks>
    private static async Task InvalidateCorsAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            // Resolved from the request scope rather than taken as a handler parameter: an embedding
            // host maps these endpoints into its own pipeline, and a minimal-API parameter it has not
            // registered is inferred as a BODY parameter, which 400s the route before the handler runs.
            if (http.RequestServices.GetService<Authagonal.Core.Clustering.IClusterEventBus>() is { } bus)
                await DynamicCorsPolicyProvider.InvalidateAsync(
                    bus, http.RequestServices.GetService<ITenantContext>(), ct);
        }
        catch (Exception) { /* the cache entry still expires; the write must not fail on a bus hiccup */ }
    }

    private static string Actor(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("email")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue("client_id")
        ?? "unknown";
}

