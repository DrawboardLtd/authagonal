using Authagonal.Core.Services;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Scim;

namespace Authagonal.Server.Endpoints.Scim;

public static class ScimGroupEndpoints
{
    public static IEndpointRouteBuilder MapScimGroupEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/scim/v2/Groups", "/scim/Groups" })
        {
            var group = app.MapGroup(prefix)
                .RequireAuthorization("ScimProvisioning");

            group.MapGet("/", ListGroupsAsync);
            group.MapGet("/{id}", GetGroupAsync);
            group.MapPost("/", CreateGroupAsync).DisableAntiforgery();
            group.MapPut("/{id}", ReplaceGroupAsync).DisableAntiforgery();
            group.MapPatch("/{id}", PatchGroupAsync).DisableAntiforgery();
            group.MapDelete("/{id}", DeleteGroupAsync);
        }

        return app;
    }

    private static string GetBaseUrl(Authagonal.Core.Services.ITenantContext tenantContext) =>
        tenantContext.Issuer;

    // SCIM groups are owned by the SCIM client that created them (stored in OrganizationId).
    // Every read/write must verify the caller owns the group, otherwise one SCIM client could
    // read, modify the membership of, or delete another client's groups.
    private static string? CallerClientId(HttpContext httpContext) =>
        httpContext.User.FindFirst("client_id")?.Value;

    private static bool OwnedByCaller(ScimGroup group, HttpContext httpContext) =>
        !string.IsNullOrEmpty(group.OrganizationId) &&
        string.Equals(group.OrganizationId, CallerClientId(httpContext), StringComparison.Ordinal);

    private static async Task<IResult> ListGroupsAsync(
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        int? startIndex,
        int? count,
        string? filter,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);
        var start = startIndex ?? 1;
        var pageSize = Math.Min(count ?? 100, 200);

        // Scope enumeration to groups owned by the calling SCIM client.
        var (groups, _) = await groupStore.ListAsync(CallerClientId(httpContext), 0, int.MaxValue, ct);

        // A filter is honoured or refused, never quietly dropped — silently listing every group answers a
        // different question than the one asked (RFC 7644 §3.4.2.2).
        if (!ScimFilterParser.TryParse(filter, out var filterExpression, out var filterError))
            return ScimResults.Error(400, "invalidFilter", filterError!);

        // Evaluated against the resource as the client would receive it, so members[...] value paths and
        // meta.* work the same way they do for users.
        var candidates = groups
            .OrderBy(g => g.CreatedAt)
            .Select(g => ScimGroupResource.FromGroup(g, baseUrl));
        if (filterExpression is not null)
            candidates = candidates.Where(r => ScimFilterEvaluator.Matches(filterExpression, r));

        var filteredList = candidates.ToList();
        var paged = filteredList
            .Skip(start - 1)
            .Take(pageSize)
            .ToList();

        var response = new ScimListResponse<ScimGroupResource>
        {
            TotalResults = filteredList.Count,
            StartIndex = start,
            ItemsPerPage = paged.Count,
            Resources = paged,
        };

        return ScimResults.Success(response);
    }

    private static async Task<IResult> GetGroupAsync(
        string id,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var group = await groupStore.GetAsync(id, ct);
        if (group is null || !OwnedByCaller(group, httpContext))
            return ScimResults.NotFound($"Group '{id}' not found");

        var baseUrl = GetBaseUrl(tenantContext);
        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    /// <summary>
    /// Drops member ids that do not name a user this SCIM client provisioned, returning the rejected ones.
    /// </summary>
    /// <remarks>
    /// Membership was taken verbatim: any id at all was stored, including one belonging to another tenant's
    /// SCIM client or naming no user. Because group membership drives role assignment through
    /// <c>IScimGroupRoleMappingStore</c>, writing an arbitrary id into a role-mapped group is a privilege
    /// path — the next token issued for that subject picks up the mapped roles. Checking ownership (not just
    /// existence) is what makes it cross-tenant-safe.
    /// </remarks>
    private static async Task<List<string>> RetainOwnedMembersAsync(
        ScimGroup group, string clientId, IUserStore userStore, CancellationToken ct)
    {
        var rejected = new List<string>();
        var kept = new List<string>();

        foreach (var memberId in group.MemberUserIds.Distinct(StringComparer.Ordinal))
        {
            var member = await userStore.GetAsync(memberId, ct);
            if (member is null ||
                !string.Equals(member.ScimProvisionedByClientId, clientId, StringComparison.Ordinal))
            {
                rejected.Add(memberId);
                continue;
            }
            kept.Add(memberId);
        }

        group.MemberUserIds.Clear();
        group.MemberUserIds.AddRange(kept);
        return rejected;
    }

    private static async Task<IResult> CreateGroupAsync(
        ScimCreateGroupRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ScimResults.BadRequest("displayName is required");

        var memberIds = request.Members?
            .Select(m => m.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];

        var clientId = CallerClientId(httpContext);
        if (string.IsNullOrEmpty(clientId))
            return ScimResults.BadRequest("Unable to determine the calling SCIM client");

        var group = new ScimGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = request.DisplayName,
            ExternalId = request.ExternalId,
            MemberUserIds = memberIds,
            OrganizationId = clientId, // owning SCIM client — enforced on all subsequent reads/writes
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Membership must name users THIS client provisioned. Group membership drives role assignment, so
        // an unchecked id in a role-mapped group is a privilege path.
        var rejectedMembers = await RetainOwnedMembersAsync(group, CallerClientId(httpContext) ?? "", userStore, ct);
        if (rejectedMembers.Count > 0)
            return ScimResults.Error(400, "invalidValue",
                "These member ids do not name users provisioned by this client: " + string.Join(", ", rejectedMembers));

        await groupStore.CreateAsync(group, ct);

        logger.LogInformation("SCIM group created: {GroupId} ({DisplayName})", group.Id, group.DisplayName);

        var createdGroup = ScimGroupResource.FromGroup(group, baseUrl);
        return ScimResults.Created(createdGroup, createdGroup.Meta?.Location);
    }

    private static async Task<IResult> ReplaceGroupAsync(
        string id,
        ScimCreateGroupRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);

        var group = await groupStore.GetAsync(id, ct);
        if (group is null || !OwnedByCaller(group, httpContext))
            return ScimResults.NotFound($"Group '{id}' not found");

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ScimResults.BadRequest("displayName is required");

        group.DisplayName = request.DisplayName;
        group.ExternalId = request.ExternalId;
        group.MemberUserIds = request.Members?
            .Select(m => m.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];
        group.UpdatedAt = DateTimeOffset.UtcNow;

        // Membership must name users THIS client provisioned. Group membership drives role assignment, so
        // an unchecked id in a role-mapped group is a privilege path.
        var rejectedMembers = await RetainOwnedMembersAsync(group, CallerClientId(httpContext) ?? "", userStore, ct);
        if (rejectedMembers.Count > 0)
            return ScimResults.Error(400, "invalidValue",
                "These member ids do not name users provisioned by this client: " + string.Join(", ", rejectedMembers));

        await groupStore.UpdateAsync(group, ct);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> PatchGroupAsync(
        string id,
        ScimPatchRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);

        var group = await groupStore.GetAsync(id, ct);
        if (group is null || !OwnedByCaller(group, httpContext))
            return ScimResults.NotFound($"Group '{id}' not found");

        var operations = request.Operations
            .Select(o => new ScimPatchApplier.PatchOperation(o.Op, o.Path, o.Value))
            .ToList();

        try
        {
            ScimPatchApplier.ApplyToGroup(group, operations);
        }
        catch (ScimPatchException ex)
        {
            return ScimResults.Error(400, ex.ScimType, ex.Message);
        }

        group.UpdatedAt = DateTimeOffset.UtcNow;
        // Membership must name users THIS client provisioned. Group membership drives role assignment, so
        // an unchecked id in a role-mapped group is a privilege path.
        var rejectedMembers = await RetainOwnedMembersAsync(group, CallerClientId(httpContext) ?? "", userStore, ct);
        if (rejectedMembers.Count > 0)
            return ScimResults.Error(400, "invalidValue",
                "These member ids do not name users provisioned by this client: " + string.Join(", ", rejectedMembers));

        await groupStore.UpdateAsync(group, ct);

        logger.LogInformation("SCIM group patched: {GroupId}", group.Id);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> DeleteGroupAsync(
        string id,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        ILogger<Program> logger,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var group = await groupStore.GetAsync(id, ct);
        if (group is null || !OwnedByCaller(group, httpContext))
            return ScimResults.NotFound($"Group '{id}' not found");

        await groupStore.DeleteAsync(id, ct);

        logger.LogInformation("SCIM group deleted: {GroupId}", group.Id);

        return ScimResults.NoContent();
    }
}
