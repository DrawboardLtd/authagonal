using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Scim;
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

    /// <summary>
    /// True when the whole filter is <c>userName eq "..."</c> or <c>externalId eq "..."</c> — the shape
    /// every IdP provisioning agent sends before a create, and the only one that maps to a blind-index
    /// point lookup. Anything richer is evaluated in memory over a paged scan.
    /// </summary>
    private static bool TryIndexedEquality(
        ScimFilterExpression? expression, out string attribute, out string value)
    {
        attribute = "";
        value = "";
        if (expression is not ScimFilterExpression.Comparison
            {
                Operator: ComparisonOperator.Eq,
                Path: { ValueFilter: null, Segments.Count: 1 } path,
                Value.String: { } stringValue,
            })
            return false;

        var name = path.Segments[0].ToLowerInvariant();
        if (name is not ("username" or "externalid"))
            return false;

        attribute = name;
        value = stringValue;
        return true;
    }

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

        // RFC 7644 §3.4.2.4: a negative count is invalid, and count=0 means "tell me the total, send
        // no resources". Both were passed straight through as the store page size, where a
        // non-positive value is meaningless — the Azure provider answers it with a 500.
        if (count is < 0)
            return ScimResults.Error(400, "invalidValue", "count must not be negative.");

        var countOnly = count == 0;
        var pageSize = Math.Clamp(count ?? 100, 1, 200);

        // A filter is honoured or refused, never quietly dropped: ServiceProviderConfig advertises filter
        // support, and a caller whose filter is ignored is answered a question they did not ask.
        if (!ScimFilterParser.TryParse(filter, out var filterExpression, out var filterError))
            return ScimResults.Error(400, "invalidFilter", filterError!);

        // Equality on an indexed attribute — the query IdP provisioning agents (Entra/Okta) hit before
        // every create/update — resolves via a point lookup (blind index), never a tenant scan.
        if (TryIndexedEquality(filterExpression, out var indexedAttribute, out var indexedValue))
        {
            var match = indexedAttribute == "username"
                ? await userStore.FindByEmailAsync(indexedValue, ct)
                : await userStore.FindByExternalIdAsync(clientId, indexedValue, ct);
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
        // Anything that isn't an indexed equality is evaluated in memory, against the resource as the
        // client would receive it. Keep consuming pages (bounded) so a sparse match can't return an
        // empty first page with a cursor and mislead the caller.
        for (var pages = 0; pages < 10; pages++)
        {
            var page = await userStore.ListByScimClientPageAsync(clientId, pageSize, nextCursor, ct);
            IEnumerable<ScimUserResource> pageResources = page.Users.Select(u => ScimUserResource.FromUser(u, baseUrl));
            if (filterExpression is not null)
                pageResources = pageResources.Where(r => ScimFilterEvaluator.Matches(filterExpression, r));

            resourcesOut.AddRange(pageResources);
            nextCursor = page.ContinuationToken;
            if (filterExpression is null || resourcesOut.Count >= pageSize || nextCursor is null)
                break;
        }

        return ScimResults.Success(new ScimListResponse<ScimUserResource>
        {
            // Only a COMPLETED listing has a knowable total. When nextCursor is present, omit it rather
            // than reporting the page size — see ScimListResponse.TotalResults.
            TotalResults = nextCursor is null ? resourcesOut.Count : null,
            StartIndex = 1,
            // count=0 asks for the total without the resources (§3.4.2.4).
            ItemsPerPage = countOnly ? 0 : resourcesOut.Count,
            Resources = countOnly ? [] : resourcesOut,
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

    /// <summary>
    /// A deliberately conservative address check: one '@' with non-empty local and domain parts, a dot in
    /// the domain, and no whitespace or control characters. Not RFC 5322 — the point is to refuse values
    /// that are not addresses at all, since a SCIM userName is stored as a PRE-VERIFIED email and becomes a
    /// storage key, a blind-index entry, and the input to email-based account linking.
    /// </summary>
    private static bool IsPlausibleEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        foreach (var c in value)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1) return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    private static async Task<IResult> CreateUserAsync(
        ScimCreateUserRequest request,
        HttpContext httpContext,
        IUserStore userStore,
        IProvisioningOrchestrator provisioning,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        IConfiguration configuration,
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

        // A SCIM-provisioned user is created with EmailConfirmed = true, so the address it names is treated
        // as proven from that moment on — which makes it the input to email-based account linking and to
        // password reset. Two guards follow from that.
        //
        // First, it must actually be an address. An unparseable userName would otherwise be stored as a
        // pre-verified email and become a storage key and an index entry.
        if (!IsPlausibleEmail(email))
            return ScimResults.BadRequest("userName must be a valid email address");

        // Second, the provisioning client may only create users in domains it is authorised for. Without
        // this, ANY SCIM token could mint a pre-verified account for any address — including a domain
        // belonging to another tenant — and that account then feeds federation auto-linking and (before
        // the SSO-only guard on forgot-password) a local password reset.
        var scimDomain = email.Split('@').Last();
        var allowedDomains = configuration
            .GetSection($"Scim:Clients:{clientId}:AllowedEmailDomains").Get<string[]>() ?? [];
        if (allowedDomains.Length > 0 &&
            !allowedDomains.Contains(scimDomain, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "SCIM client {ClientId} attempted to provision {Email} outside its allowed domains", clientId, email);
            return ScimResults.BadRequest($"Domain '{scimDomain}' is not permitted for this provisioning client");
        }

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
            IsActive = request.ActiveOnCreate,
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
            // A SCIM error body, not an ad-hoc one, and with a fixed message. The exception text comes
            // from a downstream provisioning app, so echoing it returned an internal message to an
            // external provisioning client — and it was the one response on this endpoint that was not
            // SCIM-shaped, which a conforming client cannot parse either.
            return ScimResults.Error(400, "invalidValue",
                "The directory rejected this user. Check the request against the provisioning rules for this client.");
        }

        logger.LogInformation("SCIM user created: {UserId} ({Email}) by client {ClientId}", user.Id, email, clientId);

        var resource = ScimUserResource.FromUser(user, baseUrl);
        // meta.location is already computed on the resource; pass it so the 201 carries it.
        return ScimResults.Created(resource, resource.Meta?.Location);
    }

    private static async Task<IResult> ReplaceUserAsync(
        string id,
        ScimCreateUserRequest request,
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

        // Omitted `active` leaves the flag alone. Assigning a defaulted-true value here meant a PUT that
        // never mentioned `active` silently reactivated a deprovisioned user.
        var wasActive = user.IsActive;
        if (request.Active is { } activeRequested)
            user.IsActive = activeRequested;
        // PUT replaces the whole resource — a missing preferredLanguage clears the stored locale.
        user.Locale = Locales.Normalize(request.PreferredLanguageOrLocale);

        // Same uniqueness rule as create and PATCH: an update must not repoint another user's
        // (clientId, externalId) mapping at this record.
        if (!string.IsNullOrEmpty(request.ExternalId))
        {
            var externalIdOwner = await userStore.FindByExternalIdAsync(clientId, request.ExternalId, ct);
            if (externalIdOwner is not null && !string.Equals(externalIdOwner.Id, user.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"User with externalId '{request.ExternalId}' already exists");
        }

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

        // Deactivating must REVOKE, not merely mark. PATCH and DELETE already do this; PUT did not, so
        // deprovisioning through the replace path left every refresh token and stored consent live and the
        // user kept working until each token expired on its own.
        if (wasActive && !user.IsActive)
        {
            await grantStore.RemoveAllBySubjectAsync(user.Id, ct);
            logger.LogInformation(
                "SCIM user {UserId} deactivated via PUT by client {ClientId}; grants revoked", user.Id, clientId);
        }

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

        // Report what could not be applied instead of answering 200 regardless. `patch.supported = true` is
        // advertised, so a silently-dropped operation left the directory believing a write had landed.
        IReadOnlyList<string> unsupported;
        try
        {
            unsupported = ScimPatchApplier.ApplyToUser(user, operations);
        }
        catch (ScimPatchException ex)
        {
            // A value the applier cannot read is refused rather than coerced. `active` is the case
            // that mattered: an unparseable value used to read as false and deprovision the user.
            return ScimResults.Error(400, ex.ScimType, ex.Message);
        }
        if (unsupported.Count > 0)
            return ScimResults.Error(400, "invalidPath",
                "Unsupported PATCH operation(s): " + string.Join("; ", unsupported));

        // If the patch changed the email, re-check the global index BEFORE persisting so it can't
        // repoint another account's email→userId mapping at this record (account-takeover clobber).
        if (!string.Equals(oldEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var collision = await userStore.FindByEmailAsync(user.Email, ct);
            if (collision is not null && !string.Equals(collision.Id, user.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"User with userName '{user.Email}' already exists");
        }

        // Update externalId index if changed
        // externalId uniqueness is enforced on this path too, not only on create.
        //
        // POST checks it and 409s; PUT and PATCH did not, so an update could point the
        // (clientId, externalId) index at THIS user while another user still believed it owned that
        // mapping — after which the provisioning client's next lookup by externalId resolved to the
        // wrong account, and a deprovision aimed at one user hit another.
        if (!string.IsNullOrEmpty(user.ExternalId))
        {
            var externalIdOwner = await userStore.FindByExternalIdAsync(clientId, user.ExternalId, ct);
            if (externalIdOwner is not null && !string.Equals(externalIdOwner.Id, user.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"User with externalId '{user.ExternalId}' already exists");
        }

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
