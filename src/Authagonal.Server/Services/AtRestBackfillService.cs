using Authagonal.Core.Clustering;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Rewrites existing rows to the current at-rest scheme, once, on the leader. Opt-in.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReindexUserAsync</c>, <c>EnumerateUserIdsAsync</c> and <c>MigrateProvisioningAppsAsync</c> are
/// implemented on every provider, documented as "the cold-row backfill for enabling encryption", and had
/// <b>zero production callers</b>. So the documented migration path did not exist: an operator with a running
/// deployment registers an <see cref="IFieldCipher"/> (and an <see cref="IIndexTokenizer"/>) and restarts, and
/// every row written before that moment keeps its PII in the clear forever — <c>TableUserStore</c> encrypts
/// only on write. Their index rows keep plaintext keys too; the <c>UserEmails</c> PartitionKey stays the
/// normalized email address.
/// </para>
/// <para>
/// The stores were built for this: they dual-read (current scheme first, legacy second), so a partially
/// backfilled deployment serves both, and <c>ReindexUserAsync</c> is idempotent. What was missing was
/// something to walk the set — which is why this exists and why it needed no store changes.
/// </para>
/// <para>
/// <b>Opt-in, and deliberately so.</b> It rewrites every user row and every profile-derived index row, which
/// on a large tenant is real write volume, and it is a migration rather than steady-state behaviour: an
/// operator schedules it. Leader-gated for the same reason the sweeps are, and it runs ONCE per process rather
/// than on a timer, because a completed backfill has nothing left to do and re-running it every interval would
/// be pure cost.
/// </para>
/// <para>
/// Enumeration is id-only and pages through the backend's native continuation, so it is O(N) and decrypts
/// nothing it is not about to rewrite — <c>ListAsync</c> would have been an O(N²) offset re-scan that decrypts
/// every skipped row.
/// </para>
/// </remarks>
internal sealed class AtRestBackfillService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection election,
    IOptions<AuthOptions> authOptions,
    ILogger<AtRestBackfillService> logger) : BackgroundService
{
    /// <summary>Reindexes issued at once. Bounded so a backfill cannot starve live traffic.</summary>
    private const int Concurrency = 8;

    /// <summary>How long to wait for leadership before giving up on this process's attempt.</summary>
    private static readonly TimeSpan LeadershipWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!authOptions.Value.AtRestBackfillEnabled)
            return;

        // Leadership is not instant at boot, so wait for it briefly rather than losing the run to a race.
        // A non-leader simply exits: another node is doing it.
        var deadline = DateTimeOffset.UtcNow + LeadershipWait;
        while (!election.IsLeader && DateTimeOffset.UtcNow < deadline)
        {
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        if (!election.IsLeader)
        {
            logger.LogInformation(
                "At-rest backfill skipped on this node: it is not the cluster leader.");
            return;
        }

        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "At-rest backfill failed. It is idempotent, so it can be re-run by restarting with "
                + "Auth:AtRestBackfillEnabled still set.");
        }
    }

    internal async Task<(int Users, int Failures, int ProvisioningApps)> RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();

        if (scope.ServiceProvider.GetService<IFieldCipher>() is null
            && scope.ServiceProvider.GetService<IIndexTokenizer>() is null)
        {
            logger.LogWarning(
                "At-rest backfill is enabled but neither IFieldCipher nor IIndexTokenizer is registered, so "
                + "there is no current scheme to rewrite rows INTO. Register one before running the backfill.");
            return (0, 0, 0);
        }

        logger.LogInformation("At-rest backfill starting.");

        var reindexed = 0;
        var failures = 0;
        var inFlight = new List<Task>(Concurrency);

        async Task ReindexAsync(string userId)
        {
            try
            {
                // A scope per user: IUserStore may be scoped, and a long backfill must not hold one open.
                await using var inner = scopeFactory.CreateAsyncScope();
                await inner.ServiceProvider.GetRequiredService<IUserStore>()
                    .ReindexUserAsync(userId, ct).ConfigureAwait(false);
                Interlocked.Increment(ref reindexed);
            }
            catch (Exception ex)
            {
                // One user's failure must not end the pass — the whole point is to get through the set, and
                // the operation is idempotent so a later run retries this row.
                Interlocked.Increment(ref failures);
                logger.LogWarning(ex, "At-rest backfill could not reindex user {UserId}", userId);
            }
        }

        await foreach (var userId in userStore.EnumerateUserIdsAsync(ct).ConfigureAwait(false))
        {
            inFlight.Add(ReindexAsync(userId));
            if (inFlight.Count < Concurrency) continue;

            await Task.WhenAll(inFlight).ConfigureAwait(false);
            inFlight.Clear();

            if (reindexed % 1000 == 0 && reindexed > 0)
                logger.LogInformation("At-rest backfill: {Count} user(s) reindexed so far", reindexed);
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);

        // The provisioning-app rows have their own migration, equally uncalled.
        var provisioningApps = 0;
        if (scope.ServiceProvider.GetService<IProvisioningAppStore>() is { } appStore)
        {
            try
            {
                provisioningApps = await appStore.MigrateProvisioningAppsAsync(dryRun: false, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "At-rest backfill could not migrate provisioning apps");
            }
        }

        logger.LogInformation(
            "At-rest backfill complete: {Users} user(s) reindexed, {Failures} failed, "
            + "{Apps} provisioning app(s) migrated. Turn Auth:AtRestBackfillEnabled off once satisfied — it is "
            + "idempotent, so leaving it on only costs one pass per restart.",
            reindexed, failures, provisioningApps);

        return (reindexed, failures, provisioningApps);
    }
}
