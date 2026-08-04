using Authagonal.Core.Services;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Scim;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Refuses a membership list past <see cref="AuthOptions.MaxScimGroupMembers"/>.
    /// </summary>
    /// <remarks>
    /// The list is one uncapped array on the group row and every member in it is re-verified against the
    /// user store on write, so an unbounded array is both an unbounded row and an unbounded number of
    /// point reads inside a single request.
    /// </remarks>
    /// <summary>
    /// How many PATCH operations one request may carry.
    /// </summary>
    /// <remarks>
    /// RFC 7644 sets no limit, and neither did this: an unbounded operation list multiplied whatever per-operation
    /// cost the applier has, and the membership applier is quadratic in the ids supplied. A real provisioning
    /// connector sends a handful of operations per resource.
    /// </remarks>
    private const int MaxPatchOperations = 100;

    /// <summary>
    /// How many member ids an operation's <c>value</c> names, so the cost can be bounded before it is paid.
    /// </summary>
    private static int CountSuppliedMembers(ScimPatchApplier.PatchOperation operation)
    {
        if (operation.Value is not { } value) return 0;
        return value.ValueKind == System.Text.Json.JsonValueKind.Array ? value.GetArrayLength() : 1;
    }

    private static IResult? RefuseOversizedMembership(IReadOnlyCollection<string> memberIds, AuthOptions options) =>
        memberIds.Count > options.MaxScimGroupMembers
            ? ScimResults.Error(400, "invalidValue",
                $"A group may carry at most {options.MaxScimGroupMembers} members.")
            : null;

    /// <summary>Opaque page token for the Groups collection.</summary>
    /// <remarks>
    /// The store pages by index, and a cursor is opaque by contract — the client's only obligation is to hand
    /// back what it was given — so the token carries the next start index. Prefixed and base64'd so a client
    /// cannot construct one by guessing an integer, and so a malformed value is refused rather than silently
    /// treated as page one.
    /// </remarks>
    private const string GroupCursorPrefix = "gi:";

    private static string WriteGroupCursor(int nextStart)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{GroupCursorPrefix}{nextStart}"));

    /// <summary>
    /// A cursor for the FILTERED listing, carrying the scan offset to resume from.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="WriteGroupCursor"/> because the two page over different things: the unfiltered
    /// listing pages by store index, while a filtered listing scans rows and collects the ones that match, so
    /// resuming it needs the row it stopped at. Marked with <c>f</c> so a cursor cannot be read as the wrong
    /// kind, and so tokens already issued in the old (index-only) form keep working.
    /// </remarks>
    private static string WriteFilteredGroupCursor(int scanFrom)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{GroupCursorPrefix}f{scanFrom}"));

    /// <param name="start">The next start index, for a cursor from the unfiltered listing.</param>
    /// <param name="scanFrom">The row to resume scanning at, for a cursor from the filtered listing.</param>
    /// <remarks>
    /// At most one of the two is set. A cursor issued before the filtered form existed parses as
    /// <paramref name="start"/>, which is what it meant.
    /// </remarks>
    private static bool TryReadGroupCursor(string? cursor, out int? start, out int? scanFrom)
    {
        start = null;
        scanFrom = null;
        if (string.IsNullOrEmpty(cursor)) return true;

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!decoded.StartsWith(GroupCursorPrefix, StringComparison.Ordinal)) return false;

            var body = decoded[GroupCursorPrefix.Length..];

            if (body.StartsWith('f'))
            {
                if (!int.TryParse(body[1..], out var scan) || scan < 0) return false;
                scanFrom = scan;
                return true;
            }

            if (!int.TryParse(body, out var parsed) || parsed < 1) return false;

            start = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<IResult> ListGroupsAsync(
        HttpContext httpContext,
        IScimGroupStore groupStore,
        IAuditLogger audit,
        Authagonal.Core.Services.ITenantContext tenantContext,
        int? startIndex,
        int? count,
        string? cursor,
        string? filter,
        string? attributes,
        string? excludedAttributes,
        IRateLimiter rateLimiter,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        // RFC 7644 §3.9 projection, honoured or refused — same rule as the filter below.
        if (!ScimProjection.TryCreate(attributes, excludedAttributes, out var projection, out var projectionError))
            return ScimResults.Error(400, "invalidValue", projectionError);

        var baseUrl = GetBaseUrl(tenantContext);

        // RFC 7644 §3.4.2.4, implemented on /Users and not here. A negative count was silently clamped to 1,
        // so `count=-1` returned a resource; and `count=0` — the spec's way to ask "how many are there, send me
        // none" — returned one full group INCLUDING its entire members array, which is both the wrong answer
        // and the largest possible one. Same two rules as ScimUserEndpoints, so a client cannot get different
        // count semantics depending on which collection it asks.
        if (count < 0)
            return ScimResults.Error(400, "invalidValue", "count must not be negative.");

        var countOnly = count == 0;
        var pageSize = Math.Clamp(count ?? 100, 1, 200);

        // Cursor pagination, because ServiceProviderConfig advertises it for the provider and this
        // collection did not offer it — so a client built against the advertisement could never read past the
        // first page of groups. The store is index-based, and a cursor is opaque by definition, so the token
        // carries the next start index. `startIndex` keeps working for clients already using it.
        if (!TryReadGroupCursor(cursor, out var cursorStart, out var cursorScanFrom))
            return ScimResults.Error(400, "invalidValue", "cursor is not a cursor this server issued.");

        var start = cursorStart ?? Math.Max(startIndex ?? 1, 1);

        // Enumeration is scoped to groups owned by the calling SCIM client. No backend indexes the owner, so a
        // list is a SCAN — and the scan is not bounded by the requested page. Every provider's ListAsync
        // materialises the owner's whole set, orders it, then skips and takes; the page argument bounds what is
        // RETURNED and therefore what is serialised into resources, not what is read from storage.
        //
        // Comments here used to imply otherwise ("asks the store for the page it will return"), which was
        // true of the serialisation and false of the read. What actually bounds one request's cost is the rate
        // limiter above (200/minute per client), the per-client group quota, and — under a filter — the fixed
        // window count below. Bounding the READ needs an owner index on all four providers plus a keyset
        // ListAsync; until that exists, this is a scan and saying so is the only honest option.
        var clientId = CallerClientId(httpContext);

        // A filter is honoured or refused, never quietly dropped — silently listing every group answers a
        // different question than the one asked (RFC 7644 §3.4.2.2).
        if (!ScimFilterParser.TryParse(filter, out var filterExpression, out var filterError))
            return ScimResults.Error(400, "invalidFilter", filterError!);

        // Unfiltered listing asks the store only for the page it will return, so only that page is turned into
        // resources. It used to ask for (0, int.MaxValue) and page in memory, which serialised every group in
        // the tenant to a JsonNode however small the page requested — that part is genuinely fixed. The store
        // still READS the owner's whole set to satisfy the request; see the note above.
        if (filterExpression is null)
        {
            var (page, total) = await groupStore.ListAsync(clientId, start, pageSize, ct);
            var pageResources = page.Select(g => ScimGroupResource.FromGroup(g, baseUrl)).ToList();
            var nextStart = start + pageResources.Count;
            return ScimResults.Success(new ScimListResponse<object>
            {
                TotalResults = total,
                StartIndex = start,
                // count=0 asks for the total without the resources (§3.4.2.4).
                ItemsPerPage = countOnly ? 0 : pageResources.Count,
                Resources = countOnly ? [] : ScimProjection.ApplyAll(pageResources, projection),
                // Present only while there is another page, so a cursor-following client stops.
                NextCursor = nextStart <= total ? WriteGroupCursor(nextStart) : null,
            });
        }

        // A filter has to be evaluated against the resource as the client would receive it (so members[...]
        // value paths and meta.* behave as they do for users), which means serialising candidates. That is
        // done over bounded windows rather than the whole tenant: at most MaxFilterWindows × WindowSize
        // groups are materialised for one request.
        const int WindowSize = 200;
        const int MaxFilterWindows = 10;

        var matches = new List<ScimGroupResource>();
        var matched = 0;

        // Where this request begins scanning. A filtered cursor carries the SCAN offset, not a match index:
        // resuming by match index would re-scan from row 1 every time and, past MaxFilterWindows × WindowSize
        // rows, could never reach the requested match at all. Everything before scanFrom was returned by an
        // earlier page, so matches found from here are collected without skipping.
        var scanFrom = cursorScanFrom ?? 0;
        var scanned = scanFrom;
        var skip = cursorScanFrom is null ? start - 1 : 0;
        var exhausted = false;

        for (var window = 0; window < MaxFilterWindows; window++)
        {
            var (groups, total) = await groupStore.ListAsync(clientId, scanned + 1, WindowSize, ct);
            scanned += groups.Count;

            foreach (var group in groups)
            {
                var resource = ScimGroupResource.FromGroup(group, baseUrl);
                if (!ScimFilterEvaluator.Matches(filterExpression, resource))
                    continue;
                matched++;
                if (matched > skip && matches.Count < pageSize)
                    matches.Add(resource);
            }

            exhausted = groups.Count == 0 || scanned >= total;
            if (exhausted || matches.Count >= pageSize)
                break;
        }

        return ScimResults.Success(new ScimListResponse<object>
        {
            // Only a completed scan knows the true total, and only one that began at the start of the
            // collection. Reporting what this page matched when groups past the window were never examined —
            // or when earlier pages were counted by an earlier request — would tell a syncing client it had
            // seen everything. See ScimListResponse.TotalResults.
            TotalResults = exhausted && scanFrom == 0 ? matched : null,
            StartIndex = start,
            // count=0 asks for the total without the resources (§3.4.2.4).
            ItemsPerPage = countOnly ? 0 : matches.Count,
            Resources = countOnly ? [] : ScimProjection.ApplyAll(matches, projection),
            // The gap this closes: a truncated filtered scan returned neither totalResults NOR nextCursor, so
            // a cursor-following client saw no cursor and concluded the filtered set was empty, while a
            // totalResults-reading client saw the field absent. Neither could tell "nothing matched" from
            // "the scan window ran out before it got there" — and with the matching groups sorted past the
            // window, both silently missed every one of them.
            NextCursor = exhausted ? null : WriteFilteredGroupCursor(scanned),
        });
    }

    private static async Task<IResult> GetGroupAsync(
        string id,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        IAuditLogger audit,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IRateLimiter rateLimiter,
        string? attributes,
        string? excludedAttributes,
        CancellationToken ct)
    {
        if (!ScimProjection.TryCreate(attributes, excludedAttributes, out var projection, out var projectionError))
            return ScimResults.Error(400, "invalidValue", projectionError);

        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var group = await groupStore.GetAsync(id, ct);
        if (group is null || !OwnedByCaller(group, httpContext))
            return ScimResults.NotFound($"Group '{id}' not found");

        var baseUrl = GetBaseUrl(tenantContext);

        // The projection is APPLIED, not merely validated. This endpoint parsed attributes/excludedAttributes,
        // refused an invalid one, and then returned the full resource — so
        // `?excludedAttributes=members` answered 200 with the complete members array, one entry per member with
        // a user id and a $ref, to a connector that had explicitly said it did not want it. That is the
        // documented purpose of the parameter and the reason a well-behaved connector sends it: on a
        // role-mapped group of any size it is the largest field in the response. Symmetrically,
        // `?attributes=displayName` returned externalId and the whole roster too.
        //
        // RFC 7644 §3.9 is honoured on the single-user GET, the user listing and the group listing. This was
        // the fourth read path and the only one of the four with no test.
        return ScimResults.Success(
            ScimProjection.ApplyAll([ScimGroupResource.FromGroup(group, baseUrl)], projection).Single());
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
        IAuditLogger audit,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        // The Group endpoints had no rate limiting at all, and every list is a full table scan of all
        // groups — so an authenticated SCIM token could drive unbounded scan load. Same bucket and budget as
        // the User endpoints; the limiter is tenant-scoped by its decorator.
        if (await rateLimiter.IsRateLimitedAsync($"scim|{CallerClientId(httpContext) ?? "anonymous"}", 200, TimeSpan.FromMinutes(1), ct))
            return ScimResults.Error(429, "tooMany", "Too many SCIM requests. Please try again later.");

        var baseUrl = GetBaseUrl(tenantContext);
        var options = authOptions.Value;

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return ScimResults.BadRequest("displayName is required");

        var memberIds = request.Members?
            .Select(m => m.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];
        if (RefuseOversizedMembership(memberIds!, options) is { } oversized) return oversized;

        var clientId = CallerClientId(httpContext);
        if (string.IsNullOrEmpty(clientId))
            return ScimResults.BadRequest("Unable to determine the calling SCIM client");

        // Group creation was unbounded: the rate limiter paces it at 200/min but nothing stopped a SCIM
        // token from growing its group table forever. That matters far past this endpoint, because
        // GetGroupsByUserIdAsync is an unindexed full scan of the same table and runs on EVERY token mint
        // and every /connect/userinfo call for the tenant once a group→role mapping exists — so an
        // inflated table makes every login in the tenant pay for it. The cap is what bounds that scan.
        //
        // Asks for ONE row, not MaxScimGroupsPerClient of them. Only the total is read here, and every
        // store returns the full count independently of the page it hands back — so asking for one row rather
        // than the cap's worth avoids serialising 5000 group models on every create. It does NOT avoid reading
        // them: ListAsync materialises and orders the owner's whole set either way, so this check still costs a
        // scan. Removing that needs a counted owner index, which no provider has.
        // externalId becomes the PartitionKey of the external-id index outright — see StorageKeySafety. The
        // group row is written BEFORE that index, so an unstorable value fails after the group is durably
        // created and every connector retry leaves another unreachable orphan against the per-client quota.
        if (!string.IsNullOrEmpty(request.ExternalId)
            && !StorageKeySafety.IsUsableExternalId(request.ExternalId))
            return ScimResults.BadRequest(StorageKeySafety.ExternalIdRefusal);

        // externalId uniqueness, using the index every provider already maintains.
        //
        // IScimGroupStore.FindByExternalIdAsync is implemented and indexed by all four storage providers,
        // migrated, parity-tested — and was called by no production code, so the rule it exists to serve was
        // never enforced. It is enforced on users and was enforced on nothing else.
        //
        // No attacker needed: Entra POSTs a group, the response is lost to a gateway timeout after the row is
        // written, Entra retries, and a SECOND group with the same externalId is created. The connector's later
        // PATCHes resolve the group through an `externalId eq` filter, which returns whichever row the scan
        // reaches first — so if the administrator attached a group-to-role mapping to the other one, the
        // members it grants roles to silently stop matching the members the connector is maintaining.
        if (!string.IsNullOrEmpty(request.ExternalId)
            && await groupStore.FindByExternalIdAsync(clientId, request.ExternalId, ct) is not null)
            return ScimResults.Conflict($"Group with externalId '{request.ExternalId}' already exists");

        var (_, ownedCount) = await groupStore.ListAsync(clientId, 1, 1, ct);
        if (ownedCount >= options.MaxScimGroupsPerClient)
            return ScimResults.Error(403, "invalidValue",
                $"This provisioning client already owns the maximum of {options.MaxScimGroupsPerClient} groups.");

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

        // Group membership drives role assignment through IScimGroupRoleMappingStore, so a group write is a
        // privilege change and had no audit row at all. See ScimActor.
        await audit.LogAsync(
            ScimActor.Of(httpContext), "scim.group_created", "scim_group", group.Id, group.DisplayName, ct);

        var createdGroup = ScimGroupResource.FromGroup(group, baseUrl);
        return ScimResults.Created(createdGroup, createdGroup.Meta?.Location);
    }

    private static async Task<IResult> ReplaceGroupAsync(
        string id,
        ScimCreateGroupRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        IAuditLogger audit,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        IOptions<AuthOptions> authOptions,
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

        // Same rule as create: a replace can move the index to an unstorable key just as easily.
        if (!string.IsNullOrEmpty(request.ExternalId)
            && !StorageKeySafety.IsUsableExternalId(request.ExternalId))
            return ScimResults.BadRequest(StorageKeySafety.ExternalIdRefusal);

        // Uniqueness on the update path too, not only on create — otherwise a PUT is the way around it, and
        // repointing the index at THIS group while another still believed it owned that externalId is exactly
        // the state the user path's own comment describes as unrecoverable through the API.
        if (!string.IsNullOrEmpty(request.ExternalId))
        {
            var externalIdOwner = await groupStore.FindByExternalIdAsync(
                CallerClientId(httpContext) ?? "", request.ExternalId, ct);
            if (externalIdOwner is not null
                && !string.Equals(externalIdOwner.Id, group.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"Group with externalId '{request.ExternalId}' already exists");
        }

        group.DisplayName = request.DisplayName;
        group.ExternalId = request.ExternalId;
        group.MemberUserIds = request.Members?
            .Select(m => m.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];
        if (RefuseOversizedMembership(group.MemberUserIds, authOptions.Value) is { } oversized) return oversized;
        group.UpdatedAt = DateTimeOffset.UtcNow;

        // Membership must name users THIS client provisioned. Group membership drives role assignment, so
        // an unchecked id in a role-mapped group is a privilege path.
        var rejectedMembers = await RetainOwnedMembersAsync(group, CallerClientId(httpContext) ?? "", userStore, ct);
        if (rejectedMembers.Count > 0)
            return ScimResults.Error(400, "invalidValue",
                "These member ids do not name users provisioned by this client: " + string.Join(", ", rejectedMembers));

        await groupStore.UpdateAsync(group, ct);

        await audit.LogAsync(
            ScimActor.Of(httpContext), "scim.group_replaced", "scim_group", group.Id,
            $"{group.MemberUserIds.Count} member(s)", ct);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> PatchGroupAsync(
        string id,
        ScimPatchRequest request,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        IAuditLogger audit,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        IUserStore userStore,
        IRateLimiter rateLimiter,
        IOptions<AuthOptions> authOptions,
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

        // Bounded BEFORE the applier runs, not after.
        //
        // The membership cap was enforced on the RESULT (see RefuseOversizedMembership below), so the work was
        // already done by the time the request was refused. AddGroupMembers does a Contains-then-Add against a
        // List per element and RemoveGroupMembers does a linear Remove, so both are quadratic in the number of
        // ids the caller supplies — and the caller supplies them. Nothing bounded the operation count either,
        // so one authenticated SCIM request could pin a core for a long time and the refusal at the end cost
        // the attacker nothing.
        if (operations.Count > MaxPatchOperations)
            return ScimResults.Error(400, "tooLarge",
                $"At most {MaxPatchOperations} PATCH operations are accepted per request.");

        var suppliedMembers = operations.Sum(CountSuppliedMembers);
        if (suppliedMembers > authOptions.Value.MaxScimGroupMembers)
            return ScimResults.Error(400, "tooLarge",
                $"A PATCH may not name more than {authOptions.Value.MaxScimGroupMembers} members "
                + "(Auth:MaxScimGroupMembers).");

        // Report what could not be applied instead of answering 200 regardless — the same contract the
        // user endpoint got. A membership change the applier dropped but reported as done leaves the
        // departed user holding this group's mapped roles, and the IdP never retries it.
        IReadOnlyList<string> unsupported;
        try
        {
            unsupported = ScimPatchApplier.ApplyToGroup(group, operations);
        }
        catch (ScimPatchException ex)
        {
            return ScimResults.Error(400, ex.ScimType, ex.Message);
        }
        if (unsupported.Count > 0)
            return ScimResults.Error(400, "invalidPath",
                "Unsupported PATCH operation(s): " + string.Join("; ", unsupported));

        if (RefuseOversizedMembership(group.MemberUserIds, authOptions.Value) is { } oversized) return oversized;

        // Checked AFTER the applier, because a PATCH sets externalId through a path expression rather than a
        // typed field — so the only place the resulting value is knowable is here. Same rule as create and
        // replace: externalId is the PartitionKey of the external-id index, and this write repoints it.
        if (!string.IsNullOrEmpty(group.ExternalId)
            && !StorageKeySafety.IsUsableExternalId(group.ExternalId))
            return ScimResults.BadRequest(StorageKeySafety.ExternalIdRefusal);

        // And uniqueness, for the same reason and at the same point: a PATCH sets externalId through a path
        // expression, so this is where the resulting value is knowable.
        if (!string.IsNullOrEmpty(group.ExternalId))
        {
            var patchExternalIdOwner = await groupStore.FindByExternalIdAsync(
                CallerClientId(httpContext) ?? "", group.ExternalId, ct);
            if (patchExternalIdOwner is not null
                && !string.Equals(patchExternalIdOwner.Id, group.Id, StringComparison.Ordinal))
                return ScimResults.Conflict($"Group with externalId '{group.ExternalId}' already exists");
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

        await audit.LogAsync(
            ScimActor.Of(httpContext), "scim.group_patched", "scim_group", group.Id,
            $"{group.MemberUserIds.Count} member(s)", ct);

        return ScimResults.Success(ScimGroupResource.FromGroup(group, baseUrl));
    }

    private static async Task<IResult> DeleteGroupAsync(
        string id,
        HttpContext httpContext,
        IScimGroupStore groupStore,
        IAuditLogger audit,
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

        await audit.LogAsync(
            ScimActor.Of(httpContext), "scim.group_deleted", "scim_group", group.Id, group.DisplayName, ct);

        return ScimResults.NoContent();
    }
}
