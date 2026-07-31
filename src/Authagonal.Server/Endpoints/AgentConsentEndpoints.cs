using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Authority;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints;

/// <summary>
/// The floor of the agentic invariant: a user's standing, per-agent consent. Everything here
/// pre-intersects with the live ceiling on write AND is re-intersected at every mint, so
/// neither a tampered consent body nor a later ceiling narrowing can widen a delegation.
/// </summary>
public static class AgentConsentEndpoints
{
    public static IEndpointRouteBuilder MapAgentConsentEndpoints(this IEndpointRouteBuilder app)
    {
        // What the user is being asked to allow: the agent's ceiling, rendered against the
        // connector catalog so the consent screen can speak plain language.
        app.MapGet("/consent/agents/{clientId}/info", async (
            string clientId,
            IClientStore clientStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Optional seams, resolved lazily: no agent store means no agent exists (404s
            // below); no catalog just means actions render raw.
            var agentStore = AgentStore(httpContext);
            var catalog = httpContext.RequestServices.GetService<IConnectorCatalog>()
                ?? new ConfigConnectorCatalog([]);
            var client = await clientStore.GetAsync(clientId, ct);
            var profile = await agentStore.GetAsync(clientId, ct);
            if (client is null || profile is null)
                return (IResult)TypedResults.Json(new ErrorInfoResponse { Error = "agent_not_found" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            var connectors = new List<AgentConsentConnectorView>();
            foreach (var grant in profile.Ceiling.Grants)
            {
                var descriptor = await catalog.GetAsync(grant.Type, ct);
                connectors.Add(new AgentConsentConnectorView
                {
                    Type = grant.Type,
                    DisplayName = descriptor?.DisplayName ?? grant.Type,
                    Description = descriptor?.Description,
                    Actions = grant.Actions.Select(action => new AgentConsentActionView
                    {
                        Name = action,
                        Description = descriptor?.Actions?
                            .FirstOrDefault(a => string.Equals(a.Name, action, StringComparison.Ordinal))?.Description,
                        HighRisk = descriptor?.Actions?
                            .FirstOrDefault(a => string.Equals(a.Name, action, StringComparison.Ordinal))?.HighRisk ?? false,
                        Policy = AuthorityJson.PolicyName(grant.PolicyFor(action)),
                    }).ToList(),
                });
            }

            return TypedResults.Json(new AgentConsentInfoResponse
            {
                ClientId = client.ClientId,
                ClientName = client.ClientName,
                Description = client.Description,
                LogoUri = client.LogoUri,
                Mode = AgentModes.Name(profile.Mode),
                Ceiling = ToElement(profile.Ceiling),
                Connectors = connectors,
            }, AuthagonalJsonContext.Default.AgentConsentInfoResponse);
        })
        // Its three siblings below all require authorization; this one did not, and the host sets no
        // FallbackPolicy, so an endpoint with no authorization metadata is anonymous. The body is not
        // a summary — it is the agent's complete RFC 9396 ceiling plus every per-action policy — so
        // any unauthenticated caller could enumerate exactly what each registered agent is permitted
        // to do. The screen it feeds is only ever rendered to a signed-in user.
        .RequireAuthorization();

        // Grant (or replace) standing consent. The stored floor is granted ∩ live ceiling —
        // a floor can tighten a policy (auto → ask) but never loosen or widen anything.
        app.MapPost("/consent/agents", async (
            HttpContext httpContext,
            AgentConsentRequest request,
            IGrantStore grantStore,
            IEnumerable<IAuthHook> authHooks,
            CancellationToken ct) =>
        {
            // Granting standing consent with `authority` omitted grants the agent's FULL ceiling, on
            // nothing but the ambient session cookie. SameSite=Lax blocks a cross-SITE request but not
            // a cross-ORIGIN one from a sibling host, and idp.acme.com beside app.acme.com is the
            // normal shape — so script on any same-site origin could do this to a visiting user.
            if (Services.InteractiveOriginGuard.Check(httpContext) is { } originError)
                return originError;

            var subjectId = SubjectId(httpContext);
            if (subjectId is null)
                return Results.Unauthorized();

            var profile = await AgentStore(httpContext).GetAsync(request.ClientId, ct);
            if (profile is null)
                return (IResult)TypedResults.Json(new ErrorInfoResponse { Error = "agent_not_found" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

            var floor = profile.Ceiling;
            if (request.Authority is { } authorityElement)
            {
                if (!AuthorityJson.TryParse(authorityElement.GetRawText(), out var requested))
                    return TypedResults.Json(new ErrorInfoResponse
                    {
                        Error = "invalid_request",
                        ErrorDescription = "authority must be an RFC 9396 authorization_details array",
                    }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
                floor = requested.Intersect(profile.Ceiling);
            }

            var now = DateTimeOffset.UtcNow;
            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = AgentConsent.Key(subjectId, request.ClientId),
                Type = AgentConsent.GrantType,
                SubjectId = subjectId,
                ClientId = request.ClientId,
                Data = AgentConsent.Serialize(floor, now),
                CreatedAt = now,
                ExpiresAt = now.AddYears(5), // standing consent; revocation is the exit, not expiry
            }, ct);

            await authHooks.RunOnAgentConsentChangedAsync(subjectId, request.ClientId, "granted", ct);

            return TypedResults.Json(new AgentConsentView
            {
                ClientId = request.ClientId,
                Authority = ToElement(floor),
                ConsentedAt = now,
            }, AuthagonalJsonContext.Default.AgentConsentView);
        }).RequireAuthorization();

        // The user's standing agent consents.
        app.MapGet("/consent/agents", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
            var subjectId = SubjectId(httpContext);
            if (subjectId is null)
                return Results.Unauthorized();

            var grants = await grantStore.GetBySubjectAsync(subjectId, ct);
            var consents = new List<AgentConsentListItem>();
            foreach (var grant in grants.Where(g =>
                g.Type == AgentConsent.GrantType && g.ExpiresAt > DateTimeOffset.UtcNow))
            {
                if (!AgentConsent.TryParse(grant.Data, out var floor, out var consentedAt))
                    continue;
                var client = await clientStore.GetAsync(grant.ClientId, ct);
                consents.Add(new AgentConsentListItem
                {
                    ClientId = grant.ClientId,
                    ClientName = client?.ClientName ?? grant.ClientId,
                    Authority = ToElement(floor),
                    ConsentedAt = consentedAt,
                });
            }
            return TypedResults.Json(new AgentConsentListResponse { Consents = consents },
                AuthagonalJsonContext.Default.AgentConsentListResponse);
        }).RequireAuthorization();

        // Revoke: subsequent exchanges fail with consent_required on their next mint — and
        // delegated tokens are refresh-less and short-lived, so the tail is bounded.
        app.MapDelete("/consent/agents/{clientId}", async (
            string clientId,
            HttpContext httpContext,
            IGrantStore grantStore,
            IEnumerable<IAuthHook> authHooks,
            CancellationToken ct) =>
        {
            var subjectId = SubjectId(httpContext);
            if (subjectId is null)
                return Results.Unauthorized();

            await grantStore.RemoveAsync(AgentConsent.Key(subjectId, clientId), ct);
            await authHooks.RunOnAgentConsentChangedAsync(subjectId, clientId, "revoked", ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static IAgentProfileStore AgentStore(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IAgentProfileStore>() ?? new NullAgentProfileStore();

    private static string? SubjectId(HttpContext httpContext)
    {
        var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(subjectId) ? null : subjectId;
    }

    private static JsonElement ToElement(AuthoritySet set)
    {
        using var doc = JsonDocument.Parse(AuthorityJson.Serialize(set));
        return doc.RootElement.Clone();
    }
}

public sealed class AgentConsentRequest
{
    public string ClientId { get; set; } = "";

    /// <summary>The floor the user grants, as an RFC 9396 array. Omitted = consent to the
    /// full ceiling. Always stored pre-intersected with the live ceiling.</summary>
    public JsonElement? Authority { get; set; }
}

public sealed class AgentConsentInfoResponse
{
    public string ClientId { get; set; } = "";
    public string? ClientName { get; set; }
    public string? Description { get; set; }
    public string? LogoUri { get; set; }
    public string Mode { get; set; } = "delegated";
    public JsonElement Ceiling { get; set; }
    public List<AgentConsentConnectorView> Connectors { get; set; } = [];
}

public sealed class AgentConsentConnectorView
{
    public string Type { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public List<AgentConsentActionView> Actions { get; set; } = [];
}

public sealed class AgentConsentActionView
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool HighRisk { get; set; }
    public string Policy { get; set; } = "auto";
}

public sealed class AgentConsentView
{
    public string ClientId { get; set; } = "";
    public JsonElement Authority { get; set; }
    public DateTimeOffset ConsentedAt { get; set; }
}

public sealed class AgentConsentListItem
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public JsonElement Authority { get; set; }
    public DateTimeOffset ConsentedAt { get; set; }
}

public sealed class AgentConsentListResponse
{
    public List<AgentConsentListItem> Consents { get; set; } = [];
}
