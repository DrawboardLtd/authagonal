using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Migration;

/// <summary>
/// Whether the host actually mapped <c>GET /admin/migration/status</c>.
/// </summary>
/// <remarks>
/// Set by <see cref="MigrationStatusEndpoint.MapMigrationStatusEndpoint"/>. A singleton rather than a static
/// so a test host and the app under test cannot see each other's state.
/// </remarks>
public sealed class MigrationEndpointRegistration
{
    public bool Mapped { get; private set; }

    public void MarkMapped() => Mapped = true;
}

/// <summary>
/// Says so when the migration is enabled but its status endpoint was never mapped.
/// </summary>
/// <remarks>
/// <c>MapMigrationStatusEndpoint</c> was defined and called by nobody, while <c>docs/migration.md</c> told
/// the operator twice to read the report at <c>GET /admin/migration/status</c>. The documented cutover is
/// <c>DryRun=true</c> → restart → read the report, and that GET answered 404 — which the endpoint's own
/// authorization would also produce, so it read as a permissions problem rather than a missing route. The
/// DryRun validation report is the entire point of the dry run: id charset and length violations, duplicate
/// emails, the table and column inventory, per-pass counts and the warning list.
/// <para>
/// Startup is the right place for this because it is the only moment the answer is actionable. Hosted
/// services start after the host's <c>Map*</c> calls have run, so the flag is settled by the time this
/// reads it.
/// </para>
/// </remarks>
internal sealed class MigrationStatusEndpointCheck(
    DuendeMigrationOptions options,
    MigrationEndpointRegistration registration,
    ILogger<MigrationStatusEndpointCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled || registration.Mapped)
            return Task.CompletedTask;

        logger.LogWarning(
            "The Duende migration is enabled but GET /admin/migration/status is not mapped, so the report "
            + "this run produces cannot be read over HTTP — the request will answer 404. Add "
            + "app.MapAuthagonalDuendeMigration() alongside your other Map calls. This matters most with "
            + "Migration:DryRun=true, where the report IS the deliverable.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
