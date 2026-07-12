using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;

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
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        int? startIndex,
        int? count,
        string? filter,
        string? cursor,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);
        var pageSize = Math.Min(count ?? 100, 200);
        var parsed = ScimFilterParser.Parse(filter);

        // Equality filters — the path IdP provisioning agents (Entra/Okta) hit before every
        // create/update — resolve via point lookups (blind indexes), never a tenant scan.
        if (parsed is { Operator: "eq" } eq
            && eq.Attribute.ToLowerInvariant() is "username" or "externalid")
        {
            var match = eq.Attribute.ToLowerInvariant() == "username"
                ? await userStore.FindByEmailAsync(eq.Value, ct)
                : await userStore.FindByExternalIdAsync(clientId, eq.Value, ct);
            // Same scoping as the listing: only users this SCIM client provisioned.
            var resources = match is not null
                            && string.Equals(match.ScimProvisionedByClientId, clientId, StringComparison.Ordinal)
                ? new List<ScimUserResource> { ScimUserResource.FromUser(match, baseUrl) }
                : [];
            return ScimResults.Success(new ScimListResponse<ScimUserResource>
            {
                TotalResults = resources.Count,
                StartIndex = 1,
                ItemsPerPage = resources.Count,
                Resources = resources,
            });
        }

        // F26: listing is cursor-paginated (draft-ietf-scim-cursor-pagination) — the old
        // implementation materialized and decrypted the ENTIRE client population on every request
        // to emulate startIndex. Offset pagination past the first page is no longer offered.
        if ((startIndex ?? 1) > 1)
            return ScimResults.Error(400, "invalidValue",
                "startIndex pagination is not supported; page with cursor/nextCursor instead "
                + "(pass the response's nextCursor back as ?cursor=).");

        var resourcesOut = new List<ScimUserResource>();
        var nextCursor = cursor;
        // Non-eq filters (co / displayName) apply per page; keep consuming pages (bounded) so a
        // sparse match can't return an empty first page with a cursor and mislead the client.
        for (var pages = 0; pages < 10; pages++)
        {
            var page = await userStore.ListByScimClientPageAsync(clientId, pageSize, nextCursor, ct);
            IEnumerable<AuthUser> pageUsers = page.Users;
            if (parsed is not null)
            {
                pageUsers = page.Users.Where(u =>
                {
                    var displayName = $"{u.FirstName} {u.LastName}".Trim();
                    return ScimFilterParser.Matches(parsed, u.Email, u.ExternalId, displayName);
                });
            }
            resourcesOut.AddRange(pageUsers.Select(u => ScimUserResource.FromUser(u, baseUrl)));
            nextCursor = page.ContinuationToken;
            if (parsed is null || resourcesOut.Count >= pageSize || nextCursor is null)
                break;
        }

        return ScimResults.Success(new ScimListResponse<ScimUserResource>
        {
            // The true total is unknowable without a full scan under cursor pagination; report the
            // returned count (accurate whenever nextCursor is absent, i.e. the listing completed).
            TotalResults = resourcesOut.Count,
            StartIndex = 1,
            ItemsPerPage = resourcesOut.Count,
            Resources = resourcesOut,
            NextCursor = nextCursor,
        });
    }

    private static async Task<IResult> GetUserAsync(
        string id,
        HttpContext httpContext,
        IUserStore userStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var user = await userStore.GetAsync(id, ct);
        if (user is null || !string.Equals(user.ScimProvisionedByClientId, clientId, StringComparison.Ordinal))
            return ScimResults.NotFound($"User '{id}' not found");

        var baseUrl = GetBaseUrl(tenantContext);
        return ScimResults.Success(ScimUserResource.FromUser(user, baseUrl));
    }

    private static async Task<IResult> CreateUserAsync(
        ScimCreateUserRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IProvisioningOrchestrator provisioning,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

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
            Locale = Locales.Normalize(request.PreferredLanguageOrLocale),
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
        IRateLimiter rateLimiter,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);

        var user = await userStore.GetAsync(id, ct);
        if (user is null || !string.Equals(user.ScimProvisionedByClientId, clientId, StringComparison.Ordinal))
            return ScimResults.NotFound($"User '{id}' not found");

        // Extract email
        var email = request.UserName;
        if (string.IsNullOrEmpty(email) && request.Emails?.Length > 0)
            email = request.Emails.FirstOrDefault(e => e.Primary)?.Value ?? request.Emails[0].Value;

        if (!string.IsNullOrWhiteSpace(email))
        {
            email = email.ToLowerInvariant();
            if (!string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                // Re-check the global email index so an email change can't repoint another account's
                // email at this record (account-takeover via email-index clobber).
                var collision = await userStore.FindByEmailAsync(email, ct);
                if (collision is not null && !string.Equals(collision.Id, user.Id, StringComparison.Ordinal))
                    return ScimResults.Conflict($"User with userName '{email}' already exists");
            }
            user.Email = email;
            user.NormalizedEmail = email.ToUpperInvariant();
        }

        user.FirstName = request.Name?.GivenName;
        user.LastName = request.Name?.FamilyName;
        user.IsActive = request.Active;
        // PUT replaces the whole resource — a missing preferredLanguage clears the stored locale.
        user.Locale = Locales.Normalize(request.PreferredLanguageOrLocale);

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
        IRateLimiter rateLimiter,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);

        var user = await userStore.GetAsync(id, ct);
        if (user is null || !string.Equals(user.ScimProvisionedByClientId, clientId, StringComparison.Ordinal))
            return ScimResults.NotFound($"User '{id}' not found");

        var wasActive = user.IsActive;
        var oldExternalId = user.ExternalId;
        var oldEmail = user.Email;

        var operations = request.Operations
            .Select(o => new ScimPatchApplier.PatchOperation(o.Op, o.Path, o.Value))
            .ToList();

        ScimPatchApplier.ApplyToUser(user, operations);

        // If the patch changed the email, re-check the global index BEFORE persisting so it can't
        // repoint another account's email→userId mapping at this record (account-takeover clobber).
        if (!string.Equals(oldEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var collision = await userStore.FindByEmailAsync(user.Email, ct);
            if (collision is not null && !string.Equals(collision.Id, user.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"User with userName '{user.Email}' already exists");
        }

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
        IRateLimiter rateLimiter,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var clientId = GetClientId(httpContext);
        if (await rateLimiter.IsRateLimitedAsync($"scim|{clientId}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var user = await userStore.GetAsync(id, ct);
        if (user is null || !string.Equals(user.ScimProvisionedByClientId, clientId, StringComparison.Ordinal))
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
