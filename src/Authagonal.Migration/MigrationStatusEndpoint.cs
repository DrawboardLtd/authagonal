using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Migration;

/// <summary>
/// <c>GET /admin/migration/status</c> — the current-version marker plus its last report. Gated by the
/// same <c>IdentityAdmin</c> policy as the other admin endpoints.
/// </summary>
public static class MigrationStatusEndpoint
{
    public static IEndpointRouteBuilder MapMigrationStatusEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/migration/status", GetStatusAsync)
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Migration");
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        DuendeMigrationOptions options, MigrationStateStore stateStore, CancellationToken ct)
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
