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

    private static GrantReconciliationService NewService(Tables t) =>
        new(t.Grants, t.BySubject, t.ByExpiry,
            Options.Create(new BackgroundServiceOptions
            {
                GrantReconciliationDelayMinutes = 0,       // first sweep immediately
                GrantReconciliationIntervalMinutes = 240,  // never ticks again within the test
            }),
            NullLogger<GrantReconciliationService>.Instance);

    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddDays(30);

    private static PersistedGrant Grant(string key, string? subjectId) => new()
    {
        Key = key,
        Type = "refresh_token",
        SubjectId = subjectId,
        ClientId = "client-1",
        Data = "{\"sub\":\"" + (subjectId ?? "none") + "\"}",
        CreatedAt = DateTimeOffset.UtcNow,
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
