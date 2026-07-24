using System.Text.Json;
using Authagonal.Server.Services.Cluster;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Migration;

/// <summary>
/// Runs the Duende migration exactly once per configured <see cref="DuendeMigrationOptions.Version"/>,
/// in the background, without blocking host startup. Gated on cluster leadership so only one pod runs
/// it during a RollingUpdate's transient overlap; the <c>MigrationState</c> marker enforces run-once.
///
/// Leadership is poll-only (no change event), so a watchdog polls <see cref="ClusterLeaderService.IsLeader"/>
/// and cancels the engine if this pod loses the lease mid-run — the new leader then re-runs, which is
/// safe because every pass is idempotent (skip-if-exists, deterministic MFA ids).
/// </summary>
public sealed class DuendeMigrationHostedRunner(
    DuendeMigrationOptions options,
    DuendeMigrationEngine engine,
    MigrationStateStore stateStore,
    ClusterLeaderService leaderService,
    ILogger<DuendeMigrationHostedRunner> logger) : BackgroundService
{
    private static readonly TimeSpan LeaderPollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Duende migration disabled (Migration:Enabled = false)");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Source.ConnectionString))
        {
            logger.LogWarning("Duende migration enabled but Migration:Source:ConnectionString is not set — skipping");
            return;
        }

        try
        {
            // Yield past startup, and let seed services finish, before touching anything.
            await Task.Delay(TimeSpan.FromSeconds(options.StartupDelaySeconds), stoppingToken);

            if (await AlreadyDoneAsync(stoppingToken))
                return;

            if (!await WaitForLeadershipAsync(stoppingToken))
                return;

            await RunAsLeaderAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down — nothing to do; a later start retries (marker is not Completed).
        }
        catch (Exception ex)
        {
            // Never let the migration take down the host — Duende remains authoritative until cutover.
            logger.LogError(ex, "Duende migration runner faulted");
        }
    }

    private async Task<bool> AlreadyDoneAsync(CancellationToken ct)
    {
        var marker = await stateStore.GetAsync(options.Version, ct);
        if (marker?.BlocksRerun == true)
        {
            logger.LogInformation("Duende migration version '{Version}' already completed at {At} — skipping",
                options.Version, marker.CompletedAt);
            return true;
        }
        return false;
    }

    /// <summary>Polls for leadership up to <c>LeaseWaitMinutes</c>, re-checking the marker each tick in
    /// case another pod is the leader and finishes first. Returns true once this pod is leader.</summary>
    private async Task<bool> WaitForLeadershipAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(options.LeaseWaitMinutes);
        while (!leaderService.IsLeader())
        {
            if (await AlreadyDoneAsync(ct))
                return false;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogInformation(
                    "Gave up waiting {Minutes}m for cluster leadership — a later restart will retry",
                    options.LeaseWaitMinutes);
                return false;
            }

            await Task.Delay(LeaderPollInterval, ct);
        }
        return true;
    }

    private async Task RunAsLeaderAsync(CancellationToken stoppingToken)
    {
        var started = new MigrationStateEntity
        {
            RowKey = options.Version,
            Status = MigrationStateEntity.StatusStarted,
            StartedAt = DateTimeOffset.UtcNow,
            NodeId = leaderService.NodeId,
            DryRun = options.DryRun,
        };
        await stateStore.UpsertAsync(started, stoppingToken);

        // Watchdog: cancel the engine if we lose the lease mid-run.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watchdog = WatchLeadershipAsync(linked, stoppingToken);

        try
        {
            var report = await engine.RunAsync(options, linked.Token);

            await stateStore.UpsertAsync(new MigrationStateEntity
            {
                RowKey = options.Version,
                Status = MigrationStateEntity.StatusCompleted,
                StartedAt = started.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                NodeId = leaderService.NodeId,
                DryRun = options.DryRun,
                StatsJson = JsonSerializer.Serialize(report),
            }, stoppingToken);

            logger.LogInformation(
                "Duende migration '{Version}' complete (dryRun={DryRun}): users +{Users}/~{Updated}/-{Skipped}, " +
                "clients +{Clients}, scopes +{Scopes}, roles +{Roles}, logins +{Logins}, mfa +{Mfa}, " +
                "saml {Saml}, oidc {Oidc}, ssoDomains {Sso}, warnings {Warnings}, errors {Errors}",
                options.Version, report.DryRun, report.UsersCreated, report.UsersUpdated, report.UsersSkipped,
                report.ClientsCreated, report.ScopesCreated, report.RolesCreated, report.LoginsCreated,
                report.MfaCredentialsCreated, report.SamlProvidersCreated, report.OidcProvidersCreated,
                report.SsoDomainsCreated, report.Warnings.Count, report.Errors.Count);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Lost leadership mid-run — the new leader re-runs. Record and move on.
            logger.LogWarning("Duende migration cancelled — lost cluster leadership mid-run; a new leader will re-run");
            await MarkFailedAsync("lost cluster leadership mid-run", started.StartedAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Duende migration '{Version}' failed", options.Version);
            await MarkFailedAsync(ex.Message, started.StartedAt, CancellationToken.None);
        }
        finally
        {
            await watchdog;
        }
    }

    private async Task MarkFailedAsync(string error, DateTimeOffset? startedAt, CancellationToken ct)
    {
        try
        {
            await stateStore.UpsertAsync(new MigrationStateEntity
            {
                RowKey = options.Version,
                Status = MigrationStateEntity.StatusFailed,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                NodeId = leaderService.NodeId,
                DryRun = options.DryRun,
                Error = error,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write Duende migration failure marker");
        }
    }

    /// <summary>Cancels <paramref name="linked"/> when this pod stops being the leader.</summary>
    private async Task WatchLeadershipAsync(CancellationTokenSource linked, CancellationToken stoppingToken)
    {
        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (!leaderService.IsLeader())
                {
                    await linked.CancelAsync();
                    return;
                }
                await Task.Delay(LeaderPollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Engine completed (linked cancelled) or host stopping — either way, stop watching.
        }
    }
}
