using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Authority;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints;

/// <summary>
/// The user's side of the JIT approval gate: list what agents are waiting on, approve or
/// deny. The library owns the state machine; the host owns the screen that renders it and
/// the notification channel that gets the user here (via
/// <see cref="IAuthHook.OnApprovalRequestedAsync"/>).
/// </summary>
public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        // Pending approvals for the signed-in user.
        app.MapGet("/approvals", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
            var subjectId = SubjectId(httpContext);
            if (subjectId is null)
                return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            var grants = await grantStore.GetBySubjectAsync(subjectId, ct);
            var pending = new List<ApprovalView>();
            foreach (var grant in grants.Where(g =>
                g.Type == Approval.GrantType && g.ConsumedAt is null && g.ExpiresAt > now))
            {
                var data = Approval.Parse(grant.Data);
                if (data is null || data.Status != ApprovalStatus.Pending)
                    continue;
                var client = await clientStore.GetAsync(data.ClientId, ct);
                pending.Add(new ApprovalView
                {
                    Id = data.Id,
                    ClientId = data.ClientId,
                    ClientName = client?.ClientName ?? data.ClientId,
                    PendingActions = [.. data.PendingActions],
                    Slice = ToElement(data.Slice),
                    Context = new Dictionary<string, string>(data.Context, StringComparer.Ordinal),
                    CreatedAt = data.CreatedAt,
                    ExpiresAt = grant.ExpiresAt,
                });
            }

            return TypedResults.Json(new ApprovalListResponse { Approvals = pending },
                AuthagonalJsonContext.Default.ApprovalListResponse);
        }).RequireAuthorization();

        // Resolve one. Only the delegating subject may resolve their own approval.
        app.MapPost("/approvals/{approvalId}", async (
            string approvalId,
            ApprovalDecisionRequest request,
            HttpContext httpContext,
            IGrantStore grantStore,
            IEnumerable<IAuthHook> authHooks,
            CancellationToken ct) =>
        {
            var subjectId = SubjectId(httpContext);
            if (subjectId is null)
                return Results.Unauthorized();

            if (request.Decision is not ("approve" or "deny"))
                return Error("invalid_request", "decision must be 'approve' or 'deny'", 400);

            var key = Approval.Key(approvalId);
            var grant = await grantStore.GetAsync(key, ct);
            if (grant is null || grant.Type != Approval.GrantType || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                return Error("approval_not_found", "approval not found or expired", 404);
            if (grant.ConsumedAt is not null)
                return Error("approval_consumed", "approval has already been redeemed", 409);

            var data = Approval.Parse(grant.Data);
            if (data is null)
                return Error("approval_not_found", "approval not found or expired", 404);
            if (!string.Equals(data.SubjectId, subjectId, StringComparison.Ordinal))
                return Error("forbidden", "this approval belongs to a different user", 403);
            if (data.Status != ApprovalStatus.Pending)
                return Error("approval_resolved", "approval has already been resolved", 409);

            data.Status = request.Decision == "approve" ? ApprovalStatus.Approved : ApprovalStatus.Denied;
            data.ResolvedAt = DateTimeOffset.UtcNow;
            data.ResolvedBy = subjectId;

            grant.Key = key;
            grant.Data = Approval.Serialize(data);
            await grantStore.StoreAsync(grant, ct);

            await authHooks.RunOnApprovalResolvedAsync(new ApprovalAudit(
                data.Id, data.ClientId, data.SubjectId, data.PendingActions,
                data.Status == ApprovalStatus.Approved ? "approved" : "denied"), ct);

            return TypedResults.Json(new SuccessResponse { Success = true },
                AuthagonalJsonContext.Default.SuccessResponse);
        }).RequireAuthorization();

        return app;
    }

    private static string? SubjectId(HttpContext httpContext)
    {
        var subjectId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(subjectId) ? null : subjectId;
    }

    private static IResult Error(string error, string description, int statusCode) =>
        TypedResults.Json(new ErrorInfoResponse { Error = error, ErrorDescription = description },
            AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: statusCode);

    private static JsonElement ToElement(AuthoritySet set)
    {
        using var doc = JsonDocument.Parse(AuthorityJson.Serialize(set));
        return doc.RootElement.Clone();
    }
}

public sealed class ApprovalDecisionRequest
{
    public string Decision { get; set; } = "";
}

public sealed class ApprovalView
{
    public string Id { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public List<string> PendingActions { get; set; } = [];
    public JsonElement Slice { get; set; }

    /// <summary>
    /// The host extension parameters the exchange carried — the tenant/project/workspace the approved
    /// authority will be bound to.
    /// </summary>
    /// <remarks>
    /// Surfaced because the screen showed the client, the actions and the authority slice and nothing
    /// about the context, while a context-bound exchange scopes the resulting token entirely through
    /// these. A human cannot approve "read payments" meaningfully without knowing whose payments.
    /// </remarks>
    public Dictionary<string, string> Context { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ApprovalListResponse
{
    public List<ApprovalView> Approvals { get; set; } = [];
}
