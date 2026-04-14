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
        }

        return TypedResults.Json(new UserRolesResponse { UserId = user.Id, Roles = user.Roles }, AuthagonalJsonContext.Default.UserRolesResponse);
    }

    private static async Task<IResult> UnassignRole(
        RoleAssignmentRequest request,
        IUserStore userStore,
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
        }

        return TypedResults.Json(new UserRolesResponse { UserId = user.Id, Roles = user.Roles }, AuthagonalJsonContext.Default.UserRolesResponse);
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
