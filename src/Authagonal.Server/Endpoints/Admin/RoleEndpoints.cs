using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/roles")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Roles");

        group.MapGet("/", ListRoles);
        group.MapGet("/{roleId}", GetRole);
        group.MapPost("/", CreateRole);
        group.MapPut("/{roleId}", UpdateRole);
        group.MapDelete("/{roleId}", DeleteRole);
        group.MapPost("/assign", AssignRole);
        group.MapPost("/unassign", UnassignRole);
        group.MapGet("/user/{userId}", GetUserRoles);
        group.MapGet("/{roleName}/users", ListUsersInRole);

        return app;
    }

    private static async Task<IResult> ListRoles(IRoleStore roleStore, CancellationToken ct)
    {
        var roles = await roleStore.ListAsync(ct);
        return TypedResults.Json(new RoleListResponse { Roles = roles }, AuthagonalJsonContext.Default.RoleListResponse);
    }

    private static async Task<IResult> GetRole(string roleId, IRoleStore roleStore, CancellationToken ct)
    {
        var role = await roleStore.GetAsync(roleId, ct);
        return role is null ? Results.NotFound() : Results.Ok(role);
    }

    private static async Task<IResult> CreateRole(
        CreateRoleRequest request,
        IRoleStore roleStore,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "name is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var existing = await roleStore.GetByNameAsync(request.Name, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "role_exists", ErrorDescription = $"Role '{request.Name}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        var role = new Role
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await roleStore.CreateAsync(role, ct);

        // Entitlement is carried by the role NAME (see UpdateRole), so the definition of a role, who
        // holds it and which scope gates name it are all privilege facts. None of them were audited.
        await audit.LogAsync(AdminActor.Of(http), "role.created", "role", role.Id, role.Name, ct);

        return Results.Created($"/api/v1/roles/{role.Id}", role);
    }

    /// <summary>
    /// The number of holders this endpoint will rewrite in one call. Beyond it the operation is
    /// refused rather than half-applied: a partial cascade leaves some holders entitled and reports
    /// success, which is the failure this whole change exists to remove.
    /// </summary>
    private const int MaxCascade = 1000;

    /// <remarks>
    /// A rename is a revocation in disguise. Entitlement is carried by the role NAME string in
    /// <see cref="AuthUser.Roles"/> and in <see cref="Scope.AllowedRoles"/> — nothing resolves those
    /// against the role store — so renaming the row alone left every holder entitled under the old
    /// name and every scope gate pointing at a name nobody now holds. Both sides are rewritten here,
    /// or the rename is refused.
    /// </remarks>
    private static async Task<IResult> UpdateRole(
        string roleId,
        UpdateRoleRequest request,
        IRoleStore roleStore,
        IUserStore userStore,
        IScopeStore scopeStore,
        IAuditLogger audit,
        HttpContext http,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var role = await roleStore.GetAsync(roleId, ct);
        if (role is null)
            return Results.NotFound();

        var newName = request.Name;
        var renaming = !string.IsNullOrWhiteSpace(newName)
            && !string.Equals(newName, role.Name, StringComparison.Ordinal);

        if (renaming)
        {
            var clash = await roleStore.GetByNameAsync(newName!, ct);
            if (clash is not null && !string.Equals(clash.Id, role.Id, StringComparison.Ordinal))
                return Json("role_exists", $"Role '{newName}' already exists", 409);

            var (supported, holders) = await TryListHoldersAsync(userStore, role.Name, ct);
            if (!supported)
            {
                return Json(
                    "rename_not_supported",
                    $"The configured user store does not index role membership, so the holders of " +
                    $"'{role.Name}' cannot be rewritten and would keep the old name as their live " +
                    $"entitlement. Create '{newName}' as a new role, reassign, then delete '{role.Name}'.",
                    409);
            }

            if (holders.Count >= MaxCascade)
                return Json("too_many_members", $"'{role.Name}' has at least {MaxCascade} holders — too many to rewrite in one call. Reassign in batches instead.", 409);

            var logger = loggerFactory.CreateLogger(typeof(RoleEndpoints));
            foreach (var user in holders)
            {
                if (!user.Roles.Remove(role.Name)) continue;
                if (!user.Roles.Contains(newName!, StringComparer.Ordinal))
                    user.Roles.Add(newName!);
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user, ct);
            }

            var gatesRewritten = await RewriteScopeGatesAsync(scopeStore, role.Name, newName, ct);

            logger.LogInformation(
                "Role '{OldName}' renamed to '{NewName}': rewrote {Holders} holder(s) and {Gates} scope gate(s)",
                role.Name, newName, holders.Count, gatesRewritten);

            role.Name = newName!;
        }

        if (request.Description is not null)
            role.Description = request.Description;
        role.UpdatedAt = DateTimeOffset.UtcNow;

        await roleStore.UpdateAsync(role, ct);

        // A rename rewrites every holder's entitlement string and every scope gate that named it — the
        // widest privilege edit this API offers, and it left no record of who made it.
        await audit.LogAsync(AdminActor.Of(http), "role.updated", "role", role.Id,
            renaming ? $"renamed to {role.Name}" : role.Name, ct);

        return Results.Ok(role);
    }

    /// <remarks>
    /// Deleting the role row revoked nothing: every holder kept the name in
    /// <see cref="AuthUser.Roles"/>, kept it in the <c>roles</c> claim of every token, and kept
    /// passing every <see cref="Scope.AllowedRoles"/> gate — while the admin console stopped showing
    /// the role, so the operator believed the privilege was gone. Deleting also destroyed the only
    /// API for listing holders, so recovery meant recreating the role you had just deleted.
    /// <para>
    /// So the default is now to refuse a delete that would strand entitlement, and
    /// <c>?force=true</c> performs the cascade. On a store with no role-membership index the holders
    /// cannot be found at all; there <c>force</c> means "delete the definition knowing it revokes
    /// nothing", which is at least a choice the operator makes explicitly.
    /// </para>
    /// </remarks>
    private static async Task<IResult> DeleteRole(
        string roleId,
        bool? force,
        IRoleStore roleStore,
        IUserStore userStore,
        IScopeStore scopeStore,
        IAuditLogger audit,
        HttpContext http,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var existing = await roleStore.GetAsync(roleId, ct);
        if (existing is null)
            return Results.NotFound();

        var logger = loggerFactory.CreateLogger(typeof(RoleEndpoints));
        var (supported, holders) = await TryListHoldersAsync(userStore, existing.Name, ct);

        if (!supported)
        {
            if (force != true)
            {
                return Json(
                    "members_unknown",
                    $"The configured user store does not index role membership, so the holders of " +
                    $"'{existing.Name}' cannot be enumerated or revoked. Unassign the role from each " +
                    $"user first, or retry with ?force=true to delete the definition only — which " +
                    $"leaves every current holder entitled.",
                    409);
            }

            logger.LogWarning(
                "Role '{RoleName}' force-deleted on a store with no role-membership index. Any current " +
                "holders keep the role in their token claims and still pass scope gates.",
                existing.Name);

            await RewriteScopeGatesAsync(scopeStore, existing.Name, null, ct);
            await roleStore.DeleteAsync(roleId, ct);
            await audit.LogAsync(AdminActor.Of(http), "role.deleted", "role", roleId,
                $"{existing.Name} (forced; holders unknown on this store and keep the entitlement)", ct);
            return Results.NoContent();
        }

        if (holders.Count > 0 && force != true)
        {
            return Json(
                "role_has_members",
                $"'{existing.Name}' is held by {holders.Count} user(s). Deleting it would not revoke " +
                $"it from them. Unassign it first, or retry with ?force=true to revoke and delete.",
                409);
        }

        if (holders.Count >= MaxCascade)
            return Json("too_many_members", $"'{existing.Name}' has at least {MaxCascade} holders — too many to revoke in one call. Unassign in batches instead.", 409);

        var revoked = 0;
        foreach (var user in holders)
        {
            // Ordinal, matching how effective roles are resolved — a case-variant name is a
            // different entitlement, not this one.
            if (!user.Roles.Remove(existing.Name)) continue;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
            revoked++;
        }

        var gatesCleared = await RewriteScopeGatesAsync(scopeStore, existing.Name, null, ct);
        await roleStore.DeleteAsync(roleId, ct);

        await audit.LogAsync(AdminActor.Of(http), "role.deleted", "role", roleId,
            $"{existing.Name} (revoked from {revoked} holder(s), cleared from {gatesCleared} scope gate(s))", ct);

        if (holders.Count > 0 || gatesCleared > 0)
        {
            logger.LogInformation(
                "Role '{RoleName}' deleted: revoked from {Revoked} of {Holders} listed holder(s) and cleared from {Gates} scope gate(s)",
                existing.Name, revoked, holders.Count, gatesCleared);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Holders of a role, plus whether the store could answer at all — the two are different facts
    /// and a cascade that confuses them silently revokes nothing.
    /// </summary>
    private static async Task<(bool Supported, IReadOnlyList<AuthUser> Holders)> TryListHoldersAsync(
        IUserStore userStore, string roleName, CancellationToken ct)
    {
        try
        {
            return (true, await userStore.ListUsersInRoleAsync(roleName, MaxCascade, ct));
        }
        catch (NotSupportedException)
        {
            return (false, []);
        }
    }

    /// <summary>
    /// Renames (or with a null <paramref name="newName"/>, removes) a role across every scope's
    /// <see cref="Scope.AllowedRoles"/>. Returns how many scopes changed.
    /// </summary>
    /// <remarks>
    /// These gates hold name strings too, so leaving them behind on a rename points the gate at a
    /// name nobody holds — the scope silently becomes ungrantable rather than staying attached to
    /// the role the operator renamed.
    /// </remarks>
    private static async Task<int> RewriteScopeGatesAsync(
        IScopeStore scopeStore, string oldName, string? newName, CancellationToken ct)
    {
        var changed = 0;
        foreach (var scope in await scopeStore.ListAsync(ct))
        {
            if (!scope.AllowedRoles.Remove(oldName)) continue;
            if (newName is not null && !scope.AllowedRoles.Contains(newName, StringComparer.Ordinal))
                scope.AllowedRoles.Add(newName);
            await scopeStore.UpdateAsync(scope, ct);
            changed++;
        }
        return changed;
    }

    private static IResult Json(string error, string description, int status) =>
        TypedResults.Json(
            new ErrorInfoResponse { Error = error, ErrorDescription = description },
            AuthagonalJsonContext.Default.ErrorInfoResponse,
            statusCode: status);

    private static async Task<IResult> AssignRole(
        RoleAssignmentRequest request,
        IRoleStore roleStore,
        IUserStore userStore,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleName))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "userId and roleName are required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var role = await roleStore.GetByNameAsync(request.RoleName, ct);
        if (role is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "role_not_found", ErrorDescription = $"Role '{request.RoleName}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = $"User '{request.UserId}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (!user.Roles.Contains(request.RoleName))
        {
            user.Roles.Add(request.RoleName);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);

            // Granting a role is a privilege grant that lands in the next token's `roles` claim and passes
            // every scope gate naming it. The audit trail recorded client renames but not this.
            await audit.LogAsync(AdminActor.Of(http), "role.assigned", "user", user.Id, request.RoleName, ct);

        }

        return TypedResults.Json(new UserRolesResponse { UserId = user.Id, Roles = user.Roles }, AuthagonalJsonContext.Default.UserRolesResponse);
    }

    private static async Task<IResult> UnassignRole(
        RoleAssignmentRequest request,
        IUserStore userStore,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleName))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "userId and roleName are required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found", ErrorDescription = $"User '{request.UserId}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        if (user.Roles.Remove(request.RoleName))
        {
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
            await audit.LogAsync(AdminActor.Of(http), "role.unassigned", "user", user.Id, request.RoleName, ct);
        }

        return TypedResults.Json(new UserRolesResponse { UserId = user.Id, Roles = user.Roles }, AuthagonalJsonContext.Default.UserRolesResponse);
    }

    /// <summary>
    /// The users holding a role — the counterpart to <c>GET /user/{userId}</c>, and what an admin
    /// console needs to render "who administers this" without reading every account.
    /// </summary>
    /// <remarks>
    /// Answers 404 for a role that does not exist, rather than an empty list. "Nobody holds this" and
    /// "you have misspelled the role" are different problems, and a console that cannot tell them
    /// apart shows an empty table for both.
    /// </remarks>
    private static async Task<IResult> ListUsersInRole(
        string roleName,
        IRoleStore roleStore,
        IUserStore userStore,
        int? maxResults,
        CancellationToken ct)
    {
        var role = await roleStore.GetByNameAsync(roleName, ct);
        if (role is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "role_not_found", ErrorDescription = $"Role '{roleName}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        try
        {
            var users = await userStore.ListUsersInRoleAsync(role.Name, maxResults ?? 200, ct);
            return TypedResults.Json(new RoleMembersResponse
            {
                RoleName = role.Name,
                Members = users.Select(u => new RoleMemberResponse
                {
                    UserId = u.Id,
                    Email = u.Email,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Roles = u.Roles,
                }).ToList(),
            }, AuthagonalJsonContext.Default.RoleMembersResponse);
        }
        catch (NotSupportedException ex)
        {
            // The configured store does not index role membership. Say so, rather than answering with
            // an empty membership list that reads as "nobody holds this role".
            return TypedResults.Json(new ErrorInfoResponse { Error = "not_supported", ErrorDescription = ex.Message }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 501);
        }
    }

    private static async Task<IResult> GetUserRoles(
        string userId,
        IUserStore userStore,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "user_not_found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        return TypedResults.Json(new UserRolesResponse { UserId = user.Id, Roles = user.Roles }, AuthagonalJsonContext.Default.UserRolesResponse);
    }

    public sealed class CreateRoleRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public sealed class UpdateRoleRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public sealed class RoleAssignmentRequest
    {
        public string? UserId { get; set; }
        public string? RoleName { get; set; }
    }
}
