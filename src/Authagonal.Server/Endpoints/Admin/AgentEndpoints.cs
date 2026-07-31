using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Authority;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentAdminEndpoints(this IEndpointRouteBuilder app, string policy = "IdentityAdmin")
    {
        var group = app.MapGroup("/api/v1/agents")
            .RequireAuthorization(policy)
            .WithTags("Admin - Agents");

        group.MapGet("/", ListAgents);
        group.MapGet("/{clientId}", GetAgent);
        group.MapPut("/{clientId}", UpsertAgent);
        group.MapDelete("/{clientId}", DeleteAgent);
        group.MapGet("/{clientId}/effective-grant", GetEffectiveGrant);

        return app;
    }

    private static async Task<IResult> ListAgents(HttpContext http, CancellationToken ct)
    {
        var profiles = await Store(http).GetAllAsync(ct);
        var response = new AgentProfileListResponse { Agents = profiles.Select(ToView).ToList() };
        return TypedResults.Json(response, AuthagonalJsonContext.Default.AgentProfileListResponse);
    }

    private static async Task<IResult> GetAgent(string clientId, HttpContext http, CancellationToken ct)
    {
        var profile = await Store(http).GetAsync(clientId, ct);
        if (profile is null)
            return NotFound("agent_not_found");
        return TypedResults.Json(ToView(profile), AuthagonalJsonContext.Default.AgentProfileView);
    }

    private static async Task<IResult> UpsertAgent(
        string clientId,
        AgentProfileRequest request,
        IClientStore clientStore,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var store = Store(http);
        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return NotFound("client_not_found");

        var mode = AgentModes.Parse(request.Mode);
        if (request.Mode is not (null or "delegated" or "service" or "both"))
            return BadRequest("mode must be one of: delegated, service, both");

        // An agent's client_id becomes the `sub` of an entry in the RFC 8693 `act` chain — an assertion
        // about WHICH software acted. A public client proves nothing about its identity (anyone who knows the
        // client_id can present it), so attaching a profile to one makes that assertion unauthenticated:
        // the audit trail names an actor that any party could have impersonated, and approvals recorded
        // against it are meaningless.
        if (!client.RequireClientSecret || client.ClientSecretHashes.Count == 0)
            return BadRequest(
                "an agent profile requires a confidential client: the agent's client_id is asserted in the " +
                "act chain, so it must be authenticated. Set RequireClientSecret and register a secret " +
                "(or a jwks_uri for private_key_jwt).");

        // The profile requires the grant types its mode runs on — catching the mismatch here
        // beats a runtime unauthorized_client for every task the agent ever attempts.
        if (mode is AgentMode.Delegated or AgentMode.Both &&
            !client.AllowedGrantTypes.Contains(GrantTypes.TokenExchange, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"delegated mode requires the client to allow the '{GrantTypes.TokenExchange}' grant type");
        if (mode is AgentMode.Service or AgentMode.Both &&
            !client.AllowedGrantTypes.Contains(GrantTypes.ClientCredentials, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"service mode requires the client to allow the '{GrantTypes.ClientCredentials}' grant type");

        // An omitted ceiling PRESERVES the stored one, as every other field on this endpoint does.
        //
        // It defaulted to AuthoritySet.Empty instead, and empty is deny-all — so a PUT that updated,
        // say, maxTokenLifetimeSeconds and said nothing about the ceiling silently revoked the agent's
        // entire authority. Every other field on the same request merges, so the asymmetry was the
        // bug: a partial update is the normal way to use this endpoint.
        var existingProfile = await store.GetAsync(clientId, ct);
        var ceiling = existingProfile?.Ceiling ?? AuthoritySet.Empty;
        if (request.Ceiling is { } ceilingElement &&
            !AuthorityJson.TryParse(ceilingElement.GetRawText(), out ceiling))
            return BadRequest("ceiling must be an RFC 9396 authorization_details array");

        if (request.HighRiskDefault is not (null or "auto" or "ask" or "deny"))
            return BadRequest("highRiskDefault must be one of: auto, ask, deny");

        if (request.MaxDelegationDepth is < 0 or > 8)
            return BadRequest("maxDelegationDepth must be between 0 and 8");
        if (request.MaxTokenLifetimeSeconds is < 30 or > 86400)
            return BadRequest("maxTokenLifetimeSeconds must be between 30 and 86400");

        var existing = existingProfile;
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfile
        {
            ClientId = clientId,
            Mode = mode,
            Ceiling = ceiling,
            MaxDelegationDepth = request.MaxDelegationDepth ?? existing?.MaxDelegationDepth ?? 0,
            MaxTokenLifetimeSeconds = request.MaxTokenLifetimeSeconds ?? existing?.MaxTokenLifetimeSeconds ?? 300,
            HighRiskDefault = request.HighRiskDefault is null
                ? existing?.HighRiskDefault ?? ActionPolicy.Ask
                : AuthorityJson.ParsePolicyName(request.HighRiskDefault),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = existing is null ? null : now,
        };

        await store.UpsertAsync(profile, ct);
        await audit.LogAsync(Actor(http), existing is null ? "agent.created" : "agent.updated",
            "agent", clientId, null, ct);
        return TypedResults.Json(ToView(profile), AuthagonalJsonContext.Default.AgentProfileView);
    }

    private static async Task<IResult> DeleteAgent(
        string clientId,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var store = Store(http);
        var existing = await store.GetAsync(clientId, ct);
        if (existing is null)
            return NotFound("agent_not_found");

        await store.DeleteAsync(clientId, ct);
        await audit.LogAsync(Actor(http), "agent.deleted", "agent", clientId, null, ct);
        return TypedResults.Json(new SuccessResponse { Success = true }, AuthagonalJsonContext.Default.SuccessResponse);
    }

    /// <summary>
    /// Preview of the invariant the exchange applies at mint: the ceiling alone, or — when a
    /// <c>subjectId</c> is supplied — ceiling ∩ that user's standing consent. Feeds the admin
    /// UI's live "effective grant" rail.
    /// </summary>
    private static async Task<IResult> GetEffectiveGrant(
        string clientId,
        string? subjectId,
        IGrantStore grantStore,
        HttpContext http,
        CancellationToken ct)
    {
        var profile = await Store(http).GetAsync(clientId, ct);
        if (profile is null)
            return NotFound("agent_not_found");

        var response = new EffectiveGrantResponse
        {
            ClientId = clientId,
            SubjectId = subjectId,
            Ceiling = ToElement(profile.Ceiling),
        };

        if (!string.IsNullOrEmpty(subjectId))
        {
            var grant = await grantStore.GetAsync(AgentConsent.Key(subjectId, clientId), ct);
            if (grant is not null && grant.ExpiresAt > DateTimeOffset.UtcNow &&
                AgentConsent.TryParse(grant.Data, out var floor, out _))
            {
                response.Consent = ToElement(floor);
                response.Effective = ToElement(profile.Ceiling.Intersect(floor));
            }
            else
            {
                // No consent = empty floor = empty intersection: the ceiling alone grants nothing.
                response.Effective = ToElement(AuthoritySet.Empty);
            }
        }
        else
        {
            response.Effective = ToElement(profile.Ceiling);
        }

        return TypedResults.Json(response, AuthagonalJsonContext.Default.EffectiveGrantResponse);
    }

    private static AgentProfileView ToView(AgentProfile profile) => new()
    {
        ClientId = profile.ClientId,
        Mode = AgentModes.Name(profile.Mode),
        Ceiling = ToElement(profile.Ceiling),
        MaxDelegationDepth = profile.MaxDelegationDepth,
        MaxTokenLifetimeSeconds = profile.MaxTokenLifetimeSeconds,
        HighRiskDefault = AuthorityJson.PolicyName(profile.HighRiskDefault),
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
    };

    private static JsonElement ToElement(AuthoritySet set)
    {
        using var doc = JsonDocument.Parse(AuthorityJson.Serialize(set));
        return doc.RootElement.Clone();
    }

    // Resolved via the request services (not parameter injection) so a host without agent
    // storage gets clean 404s instead of a DI/body-binding failure.
    private static IAgentProfileStore Store(HttpContext http) =>
        http.RequestServices.GetService<IAgentProfileStore>() ?? new NullAgentProfileStore();

    private static IResult NotFound(string error) =>
        TypedResults.Json(new ErrorInfoResponse { Error = error },
            AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

    private static IResult BadRequest(string description) =>
        TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = description },
            AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

    private static string Actor(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("email")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue("client_id")
        ?? "unknown";
}

public sealed class AgentProfileRequest
{
    /// <summary>"delegated" | "service" | "both". Null keeps "delegated".</summary>
    public string? Mode { get; set; }

    /// <summary>The ceiling as an RFC 9396 authorization_details array.</summary>
    public JsonElement? Ceiling { get; set; }

    public int? MaxDelegationDepth { get; set; }
    public int? MaxTokenLifetimeSeconds { get; set; }

    /// <summary>"auto" | "ask" | "deny". Null keeps the existing value (or "ask").</summary>
    public string? HighRiskDefault { get; set; }
}

public sealed class AgentProfileView
{
    public string ClientId { get; set; } = "";
    public string Mode { get; set; } = "delegated";
    public JsonElement Ceiling { get; set; }
    public int MaxDelegationDepth { get; set; }
    public int MaxTokenLifetimeSeconds { get; set; }
    public string HighRiskDefault { get; set; } = "ask";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class AgentProfileListResponse
{
    public List<AgentProfileView> Agents { get; set; } = [];
}

public sealed class EffectiveGrantResponse
{
    public string ClientId { get; set; } = "";
    public string? SubjectId { get; set; }
    public JsonElement Ceiling { get; set; }
    public JsonElement? Consent { get; set; }
    public JsonElement Effective { get; set; }
}
