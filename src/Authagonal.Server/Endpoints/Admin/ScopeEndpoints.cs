using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class ScopeEndpoints
{
    public static IEndpointRouteBuilder MapScopeAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scopes")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Scopes");

        group.MapGet("/", ListScopes);
        group.MapGet("/{name}", GetScope);
        group.MapPost("/", CreateScope);
        group.MapPut("/{name}", UpdateScope);
        group.MapDelete("/{name}", DeleteScope);

        return app;
    }

    private static async Task<IResult> ListScopes(IScopeStore scopeStore, CancellationToken ct)
    {
        var scopes = await scopeStore.ListAsync(ct);
        return TypedResults.Json(new ScopeListResponse { Scopes = scopes }, AuthagonalJsonContext.Default.ScopeListResponse);
    }

    private static async Task<IResult> GetScope(string name, IScopeStore scopeStore, CancellationToken ct)
    {
        var scope = await scopeStore.GetAsync(name, ct);
        return scope is null ? Results.NotFound() : TypedResults.Json(scope, AuthagonalJsonContext.Default.Scope);
    }

    private static async Task<IResult> CreateScope(
        CreateScopeRequest request,
        IScopeStore scopeStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "name is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var existing = await scopeStore.GetAsync(request.Name, ct);
        if (existing is not null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "scope_exists", ErrorDescription = $"Scope '{request.Name}' already exists" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 409);

        var scope = new Scope
        {
            Name = request.Name,
            DisplayName = request.DisplayName,
            Description = request.Description,
            Emphasize = request.Emphasize ?? false,
            Required = request.Required ?? false,
            Group = request.Group,
            ShowInDiscoveryDocument = request.ShowInDiscoveryDocument ?? true,
            AllowedRoles = request.AllowedRoles ?? [],
            UserClaims = request.UserClaims ?? [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        await scopeStore.CreateAsync(scope, ct);
        return Results.Created($"/api/v1/scopes/{scope.Name}", scope);
    }

    private static async Task<IResult> UpdateScope(
        string name,
        UpdateScopeRequest request,
        IScopeStore scopeStore,
        CancellationToken ct)
    {
        var scope = await scopeStore.GetAsync(name, ct);
        if (scope is null) return Results.NotFound();

        if (request.DisplayName is not null) scope.DisplayName = request.DisplayName;
        if (request.Description is not null) scope.Description = request.Description;
        if (request.Emphasize.HasValue) scope.Emphasize = request.Emphasize.Value;
        if (request.Required.HasValue) scope.Required = request.Required.Value;
        if (request.Group is not null) scope.Group = request.Group;
        if (request.ShowInDiscoveryDocument.HasValue) scope.ShowInDiscoveryDocument = request.ShowInDiscoveryDocument.Value;
        // Unlike the seeder, the admin API CAN clear the gate: an explicit empty array here means
        // "ungate this scope", which is the only way back once a gate has been set.
        if (request.AllowedRoles is not null) scope.AllowedRoles = request.AllowedRoles;
        if (request.UserClaims is not null) scope.UserClaims = request.UserClaims;
        scope.UpdatedAt = DateTimeOffset.UtcNow;

        await scopeStore.UpdateAsync(scope, ct);
        return TypedResults.Json(scope, AuthagonalJsonContext.Default.Scope);
    }

    private static async Task<IResult> DeleteScope(string name, IScopeStore scopeStore, CancellationToken ct)
    {
        var existing = await scopeStore.GetAsync(name, ct);
        if (existing is null) return Results.NotFound();

        await scopeStore.DeleteAsync(name, ct);
        return Results.NoContent();
    }

    public sealed class CreateScopeRequest
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool? Emphasize { get; set; }
        public bool? Required { get; set; }

        /// <summary>Consent-screen heading to file this scope under.</summary>
        public string? Group { get; set; }
        public bool? ShowInDiscoveryDocument { get; set; }

        /// <summary>Roles a user must hold to be granted this scope. Omit or send [] for ungated.</summary>
        public List<string>? AllowedRoles { get; set; }
        public List<string>? UserClaims { get; set; }
    }

    public sealed class UpdateScopeRequest
    {
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool? Emphasize { get; set; }
        public bool? Required { get; set; }

        /// <summary>Consent-screen heading to file this scope under.</summary>
        public string? Group { get; set; }
        public bool? ShowInDiscoveryDocument { get; set; }

        /// <summary>Roles a user must hold to be granted this scope. Send [] to remove the gate.</summary>
        public List<string>? AllowedRoles { get; set; }
        public List<string>? UserClaims { get; set; }
    }
}
