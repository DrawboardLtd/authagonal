using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Authagonal.Core.Clustering;

namespace Authagonal.Tests;

/// <summary>
/// Grant/index reconciliation against real Azure Table semantics (Azurite). Grants are seeded through
/// the real <see cref="TableGrantStore"/>, then tampered with via direct <see cref="TableClient"/>
/// deletes to manufacture the three drift shapes the sweeper repairs: orphaned grants (no subject
/// index → survive subject revocation, dangerous), stale subject index rows, and stale expiry index
/// rows. The service is a BackgroundService, so each test runs one sweep cycle by starting it with a
/// zero initial delay and a long interval, polling for the expected deletions, then stopping it.
/// </summary>
[Collection("Azurite")]
public class GrantReconciliationTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private sealed record Tables(TableClient Grants, TableClient BySubject, TableClient ByExpiry);

    private Tables CreateTables(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new Tables(T("Grants"), T("GrantsBySubject"), T("GrantsByExpiry"));
    }

    private static TableGrantStore NewStore(Tables t) =>
        new(t.Grants, t.BySubject, t.ByExpiry, EnvPartitioner.Live,
            NullLogger<TableGrantStore>.Instance, fieldCipher: null);

    /// <summary>A leader, so the sweep runs. A standalone node is a permanent leader in production too.</summary>
    private static Authagonal.Server.Services.Cluster.ClusterLeaderService Leader()
    {
        var election = new LeaderElection("test-node");
        election.MarkPermanentLeader();
        return new Authagonal.Server.Services.Cluster.ClusterLeaderService(election);
    }

    private static GrantReconciliationService NewService(Tables t, EnvPartitioner? partitioner = null) =>
        new(t.Grants, t.BySubject, t.ByExpiry,
            partitioner ?? EnvPartitioner.Live,
            Leader(),
            Options.Create(new BackgroundServiceOptions
            {
                GrantReconciliationDelayMinutes = 0,       // first sweep immediately
                GrantReconciliationIntervalMinutes = 240,  // never ticks again within the test
            }),
            NullLogger<GrantReconciliationService>.Instance);

    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddDays(30);

    /// <param name="createdAt">
    /// Defaults to an hour ago, past <c>OrphanRetentionMargin</c>. A grant younger than the margin is
    /// deliberately left alone — its subject-index write may still be in flight — so a fixture stamped
    /// <c>UtcNow</c> would assert the opposite of the intended behaviour.
    /// </param>
    private static PersistedGrant Grant(
        string key, string? subjectId, DateTimeOffset? createdAt = null) => new()
    {
        Key = key,
        Type = "refresh_token",
        SubjectId = subjectId,
        ClientId = "client-1",
        Data = "{\"sub\":\"" + (subjectId ?? "none") + "\"}",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1),
        ExpiresAt = Expiry,
    };

    private static async Task<bool> ExistsAsync(TableClient table, string pk, string rk)
    {
        try
        {
            await table.GetEntityAsync<TableEntity>(pk, rk);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Starts the service, awaits the condition (one sweep cycle is milliseconds on these tiny tables), stops it.</summary>
    private static async Task RunSweepUntilAsync(GrantReconciliationService service, Func<Task<bool>> done)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!await done())
            {
                if (DateTimeOffset.UtcNow > deadline)
                    Assert.Fail("Reconciliation sweep did not produce the expected state within 30s");
                await Task.Delay(50);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Under a non-live env prefix, a perfectly healthy grant survives the sweep.
    /// </summary>
    /// <remarks>
    /// The service rebuilds the keys <c>TableGrantStore</c> writes, and it did so with no
    /// <c>EnvPartitioner</c>: the index RowKey was built from <c>grant.PartitionKey</c>, which comes back
    /// PREFIXED while the index holds the raw hash, and the lookup used the RAW subject while the index
    /// partition is prefixed. Both halves miss, the 404 branch fires for every grant, and the PRIMARY row is
    /// deleted — so every subject-bearing grant in a sandbox, dev or per-tenant env was destroyed on the
    /// first sweep. Under the live partitioner nothing is prefixed and the keys agree, which is why every
    /// existing test in this file passes: they all use <c>EnvPartitioner.Live</c>.
    /// </remarks>
    [Fact]
    public async Task AHealthyGrantSurvivesTheSweepUnderANonLiveEnvPrefix()
    {
        var tables = CreateTables("recEnv");
        var partitioner = new EnvPartitioner("dev");
        var store = new TableGrantStore(
            tables.Grants, tables.BySubject, tables.ByExpiry, partitioner,
            NullLogger<TableGrantStore>.Instance, fieldCipher: null);

        // Written the way production writes it: nothing is drifted, nothing is orphaned.
        await store.StoreAsync(Grant("env-healthy-1", "subject-1"));
        var hashed = TableGrantStore.HashKey("env-healthy-1");

        Assert.True(await ExistsAsync(tables.Grants, partitioner.PK(hashed), GrantEntity.GrantRowKey));

        // One full sweep. There is nothing to remove, so wait for the service to have run rather than for a
        // deletion — then assert the grant is still there.
        var service = NewService(tables, partitioner);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await service.StopAsync(CancellationToken.None);

        Assert.True(await ExistsAsync(tables.Grants, partitioner.PK(hashed), GrantEntity.GrantRowKey),
            "the sweep deleted a healthy grant because it rebuilt the index keys without the env prefix");
        Assert.True(
            await ExistsAsync(tables.BySubject, partitioner.PK("subject-1"), $"refresh_token|{hashed}"),
            "the sweep deleted a healthy subject index entry");
    }

    private static string ExpiryPk(string hashedKey) => GrantByExpiryEntity.GetPartitionKey(Expiry, hashedKey);

    [Fact]
    public async Task OrphanedGrant_IsPurged_HealthyGrantAndItsIndexesUntouched()
    {
        var t = CreateTables($"recon{Guid.NewGuid():N}");
        var store = NewStore(t);

        await store.StoreAsync(Grant("healthy-handle", "user-h"));
        await store.StoreAsync(Grant("orphan-handle", "user-o"));
        var healthyHash = TableGrantStore.HashKey("healthy-handle");
        var orphanHash = TableGrantStore.HashKey("orphan-handle");

        // Manufacture the orphan: subject index row lost (e.g. partial write) → the grant would
        // survive a revoke-all-for-subject. The sweeper must delete the primary grant.
        await t.BySubject.DeleteEntityAsync("user-o", $"refresh_token|{orphanHash}");

        await RunSweepUntilAsync(NewService(t), async () =>
            !await ExistsAsync(t.Grants, orphanHash, GrantEntity.GrantRowKey)
            // its expiry row is stale once the grant is gone; the same cycle's third pass removes it
            && !await ExistsAsync(t.ByExpiry, ExpiryPk(orphanHash), orphanHash));

        // The healthy grant and both of its index rows are untouched.
        Assert.True(await ExistsAsync(t.Grants, healthyHash, GrantEntity.GrantRowKey));
        Assert.True(await ExistsAsync(t.BySubject, "user-h", $"refresh_token|{healthyHash}"));
        Assert.True(await ExistsAsync(t.ByExpiry, ExpiryPk(healthyHash), healthyHash));
    }

    /// <summary>
    /// A grant younger than the retention margin is left alone: its index write may still be in flight.
    /// </summary>
    /// <remarks>
    /// <c>TableGrantStore</c> writes the primary row and its subject index in two separate calls, so between
    /// them a live grant genuinely has no index entry. That window is one round trip, stretched to seconds by
    /// the Azure SDK's exponential retry — which happens exactly when write volume is high. A sweep landing
    /// inside it deleted the primary row: the refresh token the client had just received was gone, its next
    /// refresh returned invalid_grant, and the only trace was a Warning that reads like successful cleanup.
    /// If the racing write was the authorization-code grant, the in-flight redemption failed outright.
    /// <para>
    /// This reproduces the window directly — a grant stamped now, with no index row — and asserts the sweep
    /// leaves it. The sibling test above proves a genuinely old orphan is still collected, so the margin
    /// defers collection rather than preventing it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrantYoungerThanTheRetentionMargin_IsNotJudgedAnOrphan()
    {
        var t = CreateTables($"recon{Guid.NewGuid():N}");
        var store = NewStore(t);

        // Exactly the state the two-write path passes through: primary written, index not yet.
        await store.StoreAsync(Grant("racing-handle", "user-r", createdAt: DateTimeOffset.UtcNow));
        var racingHash = TableGrantStore.HashKey("racing-handle");
        await t.BySubject.DeleteEntityAsync("user-r", $"refresh_token|{racingHash}");

        var service = NewService(t);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.True(
            await ExistsAsync(t.Grants, racingHash, GrantEntity.GrantRowKey),
            "a grant created seconds ago was deleted as an orphan while its index write could still be in flight");
    }

    [Fact]
    public async Task StaleIndexRows_AreRemoved_WhenPrimaryGrantIsGone()
    {
        var t = CreateTables($"recon{Guid.NewGuid():N}");
        var store = NewStore(t);

        await store.StoreAsync(Grant("healthy-handle", "user-h"));
        await store.StoreAsync(Grant("stale-handle", "user-s"));
        var healthyHash = TableGrantStore.HashKey("healthy-handle");
        var staleHash = TableGrantStore.HashKey("stale-handle");

        // Manufacture stale indexes: primary grant vanished (e.g. non-tombstone-first delete crash)
        // leaving subject + expiry rows pointing at nothing.
        await t.Grants.DeleteEntityAsync(staleHash, GrantEntity.GrantRowKey);

        await RunSweepUntilAsync(NewService(t), async () =>
            !await ExistsAsync(t.BySubject, "user-s", $"refresh_token|{staleHash}")
            && !await ExistsAsync(t.ByExpiry, ExpiryPk(staleHash), staleHash));

        Assert.True(await ExistsAsync(t.Grants, healthyHash, GrantEntity.GrantRowKey));
        Assert.True(await ExistsAsync(t.BySubject, "user-h", $"refresh_token|{healthyHash}"));
        Assert.True(await ExistsAsync(t.ByExpiry, ExpiryPk(healthyHash), healthyHash));
    }

    [Fact]
    public async Task SubjectlessGrant_IsNotTreatedAsOrphan()
    {
        var t = CreateTables($"recon{Guid.NewGuid():N}");
        var store = NewStore(t);

        // client_credentials-style grant: no SubjectId → the store writes no subject index row,
        // and the orphan pass must skip it rather than delete it.
        await store.StoreAsync(Grant("no-subject-handle", subjectId: null));
        var hash = TableGrantStore.HashKey("no-subject-handle");

        // Completion sentinel: a stale expiry row referencing a nonexistent grant. The expiry pass
        // runs LAST in the cycle, so once the sentinel is deleted the orphan pass has fully finished.
        var sentinelHash = TableGrantStore.HashKey("sentinel-never-stored");
        await t.ByExpiry.AddEntityAsync(new GrantByExpiryEntity
        {
            PartitionKey = ExpiryPk(sentinelHash),
            RowKey = sentinelHash,
            Type = "refresh_token",
            ExpiresAt = Expiry,
        });

        await RunSweepUntilAsync(NewService(t), async () =>
            !await ExistsAsync(t.ByExpiry, ExpiryPk(sentinelHash), sentinelHash));

        Assert.True(await ExistsAsync(t.Grants, hash, GrantEntity.GrantRowKey));
    }
}
