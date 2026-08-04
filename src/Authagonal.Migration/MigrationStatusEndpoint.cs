using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Migration;

/// <summary>
/// <c>GET /admin/migration/status</c> — the current-version marker plus its last report. Gated by the
/// same <c>IdentityAdmin</c> policy as the other admin endpoints.
/// </summary>
/// <remarks>
/// This has to be mapped by the host: <c>Authagonal.Migration</c> references <c>Authagonal.Server</c>, so
/// <c>MapAuthagonalEndpoints</c> cannot see it, and <c>AddAuthagonalDuendeMigration</c> registers services
/// rather than routes. It was defined and called by nobody — including by the tests — while
/// <c>docs/migration.md</c> named it twice as the way to read the report, so the documented cutover's
/// verification step answered 404, indistinguishable from the policy rejecting the caller.
/// <para>
/// <see cref="MigrationStatusEndpointCheck"/> reports the still-unmapped case at startup, so the omission
/// names itself instead of surfacing as a 404 at the one moment an operator needs the report.
/// </para>
/// </remarks>
public static class MigrationStatusEndpoint
{
    public static IEndpointRouteBuilder MapMigrationStatusEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/migration/status", GetStatusAsync)
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Migration");

        // Lets the startup check tell "mapped" from "forgotten". Resolved rather than required, so mapping
        // the endpoint on a host that never called AddAuthagonalDuendeMigration is not an error.
        (app.ServiceProvider?.GetService(typeof(MigrationEndpointRegistration)) as MigrationEndpointRegistration)
            ?.MarkMapped();

        return app;
    }

    /// <summary>The documented name, matching <c>AddAuthagonalDuendeMigration</c>.</summary>
    public static IEndpointRouteBuilder MapAuthagonalDuendeMigration(this IEndpointRouteBuilder app) =>
        app.MapMigrationStatusEndpoint();

    /// <remarks>
    /// <c>[FromServices]</c> rather than relying on inference: minimal APIs infer an unregistered complex
    /// type as <c>[FromBody]</c>, and a GET does not allow an inferred body, so mapping this route threw
    /// unless <c>AddAuthagonalDuendeMigration</c> had already registered both types. Being explicit makes the
    /// route mappable in any order and resolves at request time, which is also what makes it testable.
    /// </remarks>
    private static async Task<IResult> GetStatusAsync(
        [FromServices] DuendeMigrationOptions options,
        [FromServices] MigrationStateStore stateStore,
        CancellationToken ct)
    {
        var marker = await stateStore.GetAsync(options.Version, ct);
        if (marker is null)
            return Results.Json(new MigrationStatusResponse { Version = options.Version, Status = "NotRun" });

        DuendeMigrationReport? report = null;
        if (!string.IsNullOrEmpty(marker.StatsJson))
        {
            try { report = JsonSerializer.Deserialize<DuendeMigrationReport>(marker.StatsJson); }
            catch (JsonException) { /* leave report null if the stored blob is unparseable */ }
        }

        return Results.Json(new MigrationStatusResponse
        {
            Version = marker.RowKey,
            Status = marker.Status,
            DryRun = marker.DryRun,
            StartedAt = marker.StartedAt,
            CompletedAt = marker.CompletedAt,
            NodeId = marker.NodeId,
            Error = marker.Error,
            Report = report,
        });
    }
}

public sealed class MigrationStatusResponse
{
    public string Version { get; set; } = "";
    public string Status { get; set; } = "";
    public bool DryRun { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? NodeId { get; set; }
    public string? Error { get; set; }
    public DuendeMigrationReport? Report { get; set; }
}
