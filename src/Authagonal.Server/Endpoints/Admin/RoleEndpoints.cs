using Authagonal.Core.Models;
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

        return app;
    }

    private static async Task<IResult> ListRoles(IRoleStore roleStore, CancellationToken ct)
    {
        var roles = await roleStore.ListAsync(ct);
        return Results.Ok(new { roles });
    }

    private static async Task<IResult> GetRole(string roleId, IRoleStore roleStore, CancellationToken ct)
    {
        var role = await roleStore.GetAsync(roleId, ct);
        return role is null ? Results.NotFound() : Results.Ok(role);
    }

    private static async Task<IResult> CreateRole(
        CreateRoleRequest request,
        IRoleStore roleStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "invalid_request", error_description = "name is required" });

        var existing = await roleStore.GetByNameAsync(request.Name, ct);
        if (existing is not null)
            return Results.Conflict(new { error = "role_exists", error_description = $"Role '{request.Name}' already exists" });

        var role = new Role
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await roleStore.CreateAsync(role, ct);

        return Results.Created($"/api/v1/roles/{role.Id}", role);
    }

    private static async Task<IResult> UpdateRole(
        string roleId,
        UpdateRoleRequest request,
        IRoleStore roleStore,
        CancellationToken ct)
    {
        var role = await roleStore.GetAsync(roleId, ct);
        if (role is null)
            return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(request.Name))
            role.Name = request.Name;
        if (request.Description is not null)
            role.Description = request.Description;
        role.UpdatedAt = DateTimeOffset.UtcNow;

        await roleStore.UpdateAsync(role, ct);
        return Results.Ok(role);
    }

    private static async Task<IResult> DeleteRole(
        string roleId,
        IRoleStore roleStore,
        CancellationToken ct)
    {
        var existing = await roleStore.GetAsync(roleId, ct);
        if (existing is null)
            return Results.NotFound();

        await roleStore.DeleteAsync(roleId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AssignRole(
        RoleAssignmentRequest request,
        IRoleStore roleStore,
        IUserStore userStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "userId and roleName are required" });

        var role = await roleStore.GetByNameAsync(request.RoleName, ct);
        if (role is null)
            return Results.NotFound(new { error = "role_not_found", error_description = $"Role '{request.RoleName}' not found" });

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = $"User '{request.UserId}' not found" });

        if (!user.Roles.Contains(request.RoleName))
        {
            user.Roles.Add(request.RoleName);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        return Results.Ok(new { userId = user.Id, roles = user.Roles });
    }

    private static async Task<IResult> UnassignRole(
        RoleAssignmentRequest request,
        IUserStore userStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "userId and roleName are required" });

        var user = await userStore.GetAsync(request.UserId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = $"User '{request.UserId}' not found" });

        if (user.Roles.Remove(request.RoleName))
        {
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        return Results.Ok(new { userId = user.Id, roles = user.Roles });
    }

    private static async Task<IResult> GetUserRoles(
        string userId,
        IUserStore userStore,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found" });

        return Results.Ok(new { userId = user.Id, roles = user.Roles });
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
