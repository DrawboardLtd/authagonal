using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

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

    private static async Task<IResult> ListGroupsAsync(
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        int? startIndex,
        int? count,
        string? filter,
        CancellationToken ct)
    {
        var baseUrl = GetBaseUrl(tenantContext);
        var start = startIndex ?? 1;
        var pageSize = Math.Min(count ?? 100, 200);

        var (groups, totalCount) = await groupStore.ListAsync(null, 1, int.MaxValue, ct);

        // Apply filter
        var parsed = ScimFilterParser.Parse(filter);
        IEnumerable<ScimGroup> filtered = groups;
        if (parsed is not null)
        {
            filtered = groups.Where(g =>
                ScimFilterParser.MatchesGroup(parsed, g.DisplayName, g.ExternalId));
        }

        var filteredList = filtered.ToList();
        var paged = filteredList
            .OrderBy(g => g.CreatedAt)
            .Skip(start - 1)
            .Take(pageSize)
            .Select(g => ScimGroupResource.FromGroup(g, baseUrl))
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
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var group = await groupStore.GetAsync(id, ct);
        if (group is null)
            return ScimResults.NotFound($"Group '{id}' not found");

        var baseUrl = GetBaseUrl(tenantContext);
        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> CreateGroupAsync(
        ScimCreateGroupRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var baseUrl = GetBaseUrl(tenantContext);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ScimResults.BadRequest("displayName is required");

        var memberIds = request.Members?
            .Select(m => m.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];

        var group = new ScimGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = request.DisplayName,
            ExternalId = request.ExternalId,
            MemberUserIds = memberIds,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await groupStore.CreateAsync(group, ct);

        logger.LogInformation("SCIM group created: {GroupId} ({DisplayName})", group.Id, group.DisplayName);

        return ScimResults.Created(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> ReplaceGroupAsync(
        string id,
        ScimCreateGroupRequest request,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var baseUrl = GetBaseUrl(tenantContext);

        var group = await groupStore.GetAsync(id, ct);
        if (group is null)
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

        await groupStore.UpdateAsync(group, ct);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> PatchGroupAsync(
        string id,
        ScimPatchRequest request,
        IScimGroupStore groupStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var baseUrl = GetBaseUrl(tenantContext);

        var group = await groupStore.GetAsync(id, ct);
        if (group is null)
            return ScimResults.NotFound($"Group '{id}' not found");

        var operations = request.Operations
            .Select(o => new ScimPatchApplier.PatchOperation(o.Op, o.Path, o.Value))
            .ToList();

        ScimPatchApplier.ApplyToGroup(group, operations);

        group.UpdatedAt = DateTimeOffset.UtcNow;
        await groupStore.UpdateAsync(group, ct);

        logger.LogInformation("SCIM group patched: {GroupId}", group.Id);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> DeleteGroupAsync(
        string id,
        IScimGroupStore groupStore,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var group = await groupStore.GetAsync(id, ct);
        if (group is null)
            return ScimResults.NotFound($"Group '{id}' not found");

        await groupStore.DeleteAsync(id, ct);

        logger.LogInformation("SCIM group deleted: {GroupId}", group.Id);

        return ScimResults.NoContent();
    }
}
