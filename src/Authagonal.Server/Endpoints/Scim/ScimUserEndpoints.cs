using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Scim;

public static class ScimUserEndpoints
{
    public static IEndpointRouteBuilder MapScimUserEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/scim/v2/Users", "/scim/Users" })
        {
            var group = app.MapGroup(prefix)
                .RequireAuthorization("ScimProvisioning");

            group.MapGet("/", ListUsersAsync);
            group.MapGet("/{id}", GetUserAsync);
            group.MapPost("/", CreateUserAsync).DisableAntiforgery();
            group.MapPut("/{id}", ReplaceUserAsync).DisableAntiforgery();
            group.MapPatch("/{id}", PatchUserAsync).DisableAntiforgery();
            group.MapDelete("/{id}", DeleteUserAsync);
        }

        return app;
    }

    private static string GetClientId(HttpContext ctx) =>
        ctx.User.FindFirst("client_id")?.Value ?? "";

    private static string GetBaseUrl(Authagonal.Core.Services.ITenantContext tenantContext) =>
        tenantContext.Issuer;

    private static async Task<IResult> ListUsersAsync(
        HttpContext httpContext,
        IUserStore userStore,
        IClientStore clientStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        int? startIndex,
        int? count,
        string? filter,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        var baseUrl = GetBaseUrl(tenantContext);
        var start = startIndex ?? 1;
        var pageSize = Math.Min(count ?? 100, 200);

        // Resolve client to get organization scoping
        var client = await clientStore.GetAsync(clientId, ct);
        string? orgId = null;
        // Use ScimProvisionedByClientId for scoping - users created by this SCIM client
        // For now, we scope by listing all users and filtering

        var (users, totalCount) = await userStore.ListAsync(orgId, 1, int.MaxValue, ct);

        // Apply filter
        var parsed = ScimFilterParser.Parse(filter);
        IEnumerable<AuthUser> filtered = users;
        if (parsed is not null)
        {
            filtered = users.Where(u =>
            {
                var displayName = $"{u.FirstName} {u.LastName}".Trim();
                return ScimFilterParser.Matches(parsed, u.Email, u.ExternalId, displayName);
            });
        }

        var filteredList = filtered.ToList();
        var paged = filteredList
            .OrderBy(u => u.CreatedAt)
            .Skip(start - 1)
            .Take(pageSize)
            .Select(u => ScimUserResource.FromUser(u, baseUrl))
            .ToList();

        var response = new ScimListResponse<ScimUserResource>
        {
            TotalResults = filteredList.Count,
            StartIndex = start,
            ItemsPerPage = paged.Count,
            Resources = paged,
        };

        return ScimResults.Success(response);
    }

    private static async Task<IResult> GetUserAsync(
        string id,
        IUserStore userStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var user = await userStore.GetAsync(id, ct);
        if (user is null)
            return ScimResults.NotFound($"User '{id}' not found");

        var baseUrl = GetBaseUrl(tenantContext);
        return ScimResults.Success(ScimUserResource.FromUser(user, baseUrl));
    }

    private static async Task<IResult> CreateUserAsync(
        ScimCreateUserRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IClientStore clientStore,
        IProvisioningOrchestrator provisioning,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        var baseUrl = GetBaseUrl(tenantContext);

        // Extract email from userName or emails array
        var email = request.UserName;
        if (string.IsNullOrEmpty(email) && request.Emails?.Length > 0)
            email = request.Emails.FirstOrDefault(e => e.Primary)?.Value ?? request.Emails[0].Value;

        if (string.IsNullOrWhiteSpace(email))
            return ScimResults.BadRequest("userName is required");

        email = email.ToLowerInvariant();

        // Check if user already exists
        var existing = await userStore.FindByEmailAsync(email, ct);
        if (existing is not null)
            return ScimResults.Conflict($"User with userName '{email}' already exists");

        // Check externalId uniqueness
        if (!string.IsNullOrEmpty(request.ExternalId))
        {
            var byExtId = await userStore.FindByExternalIdAsync(clientId, request.ExternalId, ct);
            if (byExtId is not null)
                return ScimResults.Conflict($"User with externalId '{request.ExternalId}' already exists");
        }

        var firstName = request.Name?.GivenName;
        var lastName = request.Name?.FamilyName;

        // Fall back to displayName for name parsing
        if (string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(request.DisplayName))
        {
            var parts = request.DisplayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            firstName = parts.Length > 0 ? parts[0] : null;
            lastName = parts.Length > 1 ? parts[1] : null;
        }

        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true, // SCIM-provisioned users are pre-confirmed (SSO-only)
            FirstName = firstName,
            LastName = lastName,
            ExternalId = request.ExternalId,
            IsActive = request.Active,
            ScimProvisionedByClientId = clientId,
            LockoutEnabled = true,
            SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userStore.CreateAsync(user, ct);

        // Store externalId index
        if (!string.IsNullOrEmpty(request.ExternalId))
        {
            await userStore.SetExternalIdAsync(user.Id, clientId, request.ExternalId, ct);
        }

        // Trigger TCC provisioning
        // Provision to downstream apps (TCC)
        try
        {
            await provisioning.ProvisionAsync(user, ct);
        }
        catch (ProvisioningException ex)
        {
            await userStore.DeleteAsync(user.Id, ct);
            logger.LogWarning(ex, "Provisioning rejected SCIM user {UserId}", user.Id);
            return Results.UnprocessableEntity(new { error = "provisioning_rejected", message = ex.Message });
        }

        logger.LogInformation("SCIM user created: {UserId} ({Email}) by client {ClientId}", user.Id, email, clientId);

        var resource = ScimUserResource.FromUser(user, baseUrl);
        return ScimResults.Created(resource);
    }

    private static async Task<IResult> ReplaceUserAsync(
        string id,
        ScimCreateUserRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        var baseUrl = GetBaseUrl(tenantContext);

        var user = await userStore.GetAsync(id, ct);
        if (user is null)
            return ScimResults.NotFound($"User '{id}' not found");

        // Extract email
        var email = request.UserName;
        if (string.IsNullOrEmpty(email) && request.Emails?.Length > 0)
            email = request.Emails.FirstOrDefault(e => e.Primary)?.Value ?? request.Emails[0].Value;

        if (!string.IsNullOrWhiteSpace(email))
        {
            email = email.ToLowerInvariant();
            user.Email = email;
            user.NormalizedEmail = email.ToUpperInvariant();
        }

        user.FirstName = request.Name?.GivenName;
        user.LastName = request.Name?.FamilyName;
        user.IsActive = request.Active;

        // Update externalId
        var oldExternalId = user.ExternalId;
        user.ExternalId = request.ExternalId;

        if (!string.Equals(oldExternalId, request.ExternalId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(oldExternalId))
                await userStore.RemoveExternalIdAsync(user.Id, clientId, oldExternalId, ct);
            if (!string.IsNullOrEmpty(request.ExternalId))
                await userStore.SetExternalIdAsync(user.Id, clientId, request.ExternalId, ct);
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        return ScimResults.Success(ScimUserResource.FromUser(user, baseUrl));
    }

    private static async Task<IResult> PatchUserAsync(
        string id,
        ScimPatchRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IGrantStore grantStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        var baseUrl = GetBaseUrl(tenantContext);

        var user = await userStore.GetAsync(id, ct);
        if (user is null)
            return ScimResults.NotFound($"User '{id}' not found");

        var wasActive = user.IsActive;
        var oldExternalId = user.ExternalId;

        var operations = request.Operations
            .Select(o => new ScimPatchApplier.PatchOperation(o.Op, o.Path, o.Value))
            .ToList();

        ScimPatchApplier.ApplyToUser(user, operations);

        // Update externalId index if changed
        if (!string.Equals(oldExternalId, user.ExternalId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(oldExternalId))
                await userStore.RemoveExternalIdAsync(user.Id, clientId, oldExternalId, ct);
            if (!string.IsNullOrEmpty(user.ExternalId))
                await userStore.SetExternalIdAsync(user.Id, clientId, user.ExternalId, ct);
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // If deactivated, revoke all grants
        if (wasActive && !user.IsActive)
        {
            await grantStore.RemoveAllBySubjectAsync(user.Id, ct);
            logger.LogInformation("SCIM deactivated user {UserId}, grants revoked", user.Id);
        }

        return ScimResults.Success(ScimUserResource.FromUser(user, baseUrl));
    }

    private static async Task<IResult> DeleteUserAsync(
        string id,
        HttpContext httpContext,
        IUserStore userStore,
        IGrantStore grantStore,
        IProvisioningOrchestrator provisioning,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);

        var user = await userStore.GetAsync(id, ct);
        if (user is null)
            return ScimResults.NotFound($"User '{id}' not found");

        // Soft delete: deactivate
        user.IsActive = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);

        // Revoke all grants
        await grantStore.RemoveAllBySubjectAsync(user.Id, ct);

        // Trigger deprovisioning
        try
        {
            await provisioning.DeprovisionAllAsync(user.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SCIM deprovisioning failed for user {UserId}", user.Id);
        }

        logger.LogInformation("SCIM soft-deleted user {UserId} by client {ClientId}", user.Id, clientId);

        return ScimResults.NoContent();
    }
}
