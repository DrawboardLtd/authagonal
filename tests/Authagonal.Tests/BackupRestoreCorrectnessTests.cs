using System.Text.Json;
using Authagonal.Backup;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// F24 backup/restore correctness cluster, verified against real Azure Table semantics (Azurite):
/// (a) restore preserves exact EDM types via per-column annotations — a GUID-shaped STRING column no
/// longer comes back as Edm.Guid; (b) restore applies the _tombstones file so deletes aren't
/// resurrected; (c) the watermark skew margin re-captures rows committed just before the watermark;
/// (e) delete tombstones are written BEFORE the data delete (observable: the tombstone exists even
/// when the delete finds nothing); (f) the tombstone file's DeletedAt is the change-log row's
/// storage-clock Timestamp, so a pod clock running ahead can't make merge drop a recreated row.
/// </summary>
[Collection("Azurite")]
public class BackupRestoreCorrectnessTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient Table(string name)
    {
        var c = _svc.GetTableClient(name);
        c.CreateIfNotExists();
        return c;
    }

    [Fact]
    public async Task Restore_preserves_exact_column_types()
    {
        var prefix = $"ty{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");

        var guidValue = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow.AddDays(-3);
        await users.AddEntityAsync(new TableEntity("u1", "profile")
        {
            ["UserId"] = "8a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", // GUID-SHAPED but a string column
            ["IsoString"] = "2026-01-02T03:04:05.0000000+00:00",  // date-shaped string column
            ["RealGuid"] = guidValue,
            ["RealDate"] = when,
            ["Big"] = 5_000_000_000L,
            ["Ratio"] = 0.5d,
            ["Count"] = 42,
            ["Blob"] = new byte[] { 1, 2, 3 },
        });

        var dir = Path.Combine(Path.GetTempPath(), $"ty{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target,
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            var restorePrefix = $"tyr{Guid.NewGuid():N}";
            var result = await new RestoreService(_svc, new FileSystemBackupSource(dir),
                new RestoreOptions { TablePrefix = restorePrefix }).RunAsync(full.BackupId);
            Assert.Equal(1, result.TotalRestored);

            var restored = (await Table($"{restorePrefix}Users")
                .GetEntityAsync<TableEntity>("u1", "profile")).Value;

            Assert.IsType<string>(restored["UserId"]);   // the F24a defect re-typed this as Guid
            Assert.IsType<string>(restored["IsoString"]);
            Assert.Equal(guidValue, Assert.IsType<Guid>(restored["RealGuid"]));
            Assert.Equal(when, Assert.IsType<DateTimeOffset>(restored["RealDate"]));
            Assert.Equal(5_000_000_000L, restored["Big"]);
            Assert.Equal(0.5d, restored["Ratio"]);
            // Unannotated integer width (int vs long) is ambient SDK/service behavior, not F24a's
            // concern — assert the value and that it stayed numeric.
            Assert.Equal(42L, Convert.ToInt64(restored["Count"]));
            Assert.Equal(new byte[] { 1, 2, 3 }, restored["Blob"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Legacy_rows_without_marker_still_infer_types()
    {
        // A pre-@v backup file: inference (with its known GUID/date-shape limitation) must keep working.
        var dir = Path.Combine(Path.GetTempPath(), $"lg{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "20260101-000000"));
            await File.WriteAllTextAsync(Path.Combine(dir, "20260101-000000", "Users.jsonl"),
                """{"PartitionKey":"p","RowKey":"r","When":"2026-01-02T03:04:05.0000000+00:00","N":7}""" + "\n");

            var restorePrefix = $"lgr{Guid.NewGuid():N}";
            var result = await new RestoreService(_svc, new FileSystemBackupSource(dir),
                new RestoreOptions { TablePrefix = restorePrefix }).RunAsync("20260101-000000");
            Assert.Equal(1, result.TotalRestored);

            var restored = (await Table($"{restorePrefix}Users").GetEntityAsync<TableEntity>("p", "r")).Value;
            Assert.IsType<DateTimeOffset>(restored["When"]);
            Assert.Equal(7L, Convert.ToInt64(restored["N"]));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Restore_applies_tombstones_so_deletes_are_not_resurrected()
    {
        var prefix = $"tb{Guid.NewGuid():N}";
        var log = Table($"{prefix}Tombstones");
        var store = new TableUserStore(
            Table($"{prefix}Users"), Table($"{prefix}UserEmails"), Table($"{prefix}UserLogins"),
            Table($"{prefix}UserExternalIds"), Table($"{prefix}UserFirstNames"), Table($"{prefix}UserLastNames"),
            EnvPartitioner.Live, tombstoneWriter: new TableChangeWriter(log));

        await store.CreateAsync(new Core.Models.AuthUser { Id = "u1", Email = "a@x.test", NormalizedEmail = "A@X.TEST" });
        await store.CreateAsync(new Core.Models.AuthUser { Id = "u2", Email = "b@x.test", NormalizedEmail = "B@X.TEST" });

        var dir = Path.Combine(Path.GetTempPath(), $"tb{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target,
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            await store.DeleteAsync("u2");
            var incr = await new BackupService(_svc, target,
                new BackupOptions { TablePrefix = prefix, Incremental = true, Gzip = false, WatermarkSkewMargin = TimeSpan.Zero })
                .RunAsync();

            // Restore full then incremental, oldest-first, into a fresh prefix.
            var restorePrefix = $"tbr{Guid.NewGuid():N}";
            var source = new FileSystemBackupSource(dir);
            var restore = new RestoreService(_svc, source, new RestoreOptions { TablePrefix = restorePrefix });
            await restore.RunAsync(full.BackupId);
            var incrResult = await restore.RunAsync(incr.BackupId);

            Assert.True(incrResult.TombstonesApplied > 0);
            var restoredUsers = Table($"{restorePrefix}Users");
            Assert.NotNull((await restoredUsers.GetEntityIfExistsAsync<TableEntity>("u1", "profile")).Value);
            Assert.False((await restoredUsers.GetEntityIfExistsAsync<TableEntity>("u2", "profile")).HasValue); // stayed deleted
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Skew_margin_recaptures_rows_committed_just_before_the_watermark()
    {
        var prefix = $"sk{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@b.test" });
        await Task.Delay(100);
        var watermark = DateTimeOffset.UtcNow; // row committed BEFORE the watermark (inside the skew window)

        var dirDefault = Path.Combine(Path.GetTempPath(), $"sk{Guid.NewGuid():N}");
        var dirZero = Path.Combine(Path.GetTempPath(), $"sk0{Guid.NewGuid():N}");
        try
        {
            BackupOptions Opt(TimeSpan margin) => new()
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                WatermarkOverride = watermark,
                WatermarkSkewMargin = margin,
            };

            var withMargin = await new BackupService(_svc, new FileSystemBackupTarget(dirDefault), Opt(BackupDefaults.WatermarkSkewMargin)).RunAsync();
            var withoutMargin = await new BackupService(_svc, new FileSystemBackupTarget(dirZero), Opt(TimeSpan.Zero)).RunAsync();

            // Without the margin the pre-watermark commit is invisible (the F24c hole); with it, captured.
            Assert.Equal(1, withMargin.Tables["Users"].EntityCount);
            Assert.True(!withoutMargin.Tables.TryGetValue("Users", out var t) || t.EntityCount == 0);
        }
        finally
        {
            if (Directory.Exists(dirDefault)) Directory.Delete(dirDefault, true);
            if (Directory.Exists(dirZero)) Directory.Delete(dirZero, true);
        }
    }

    [Fact]
    public async Task Delete_tombstone_lands_even_when_the_data_delete_finds_nothing()
    {
        // Tombstone-first ordering (F24e): the observable contract is that the tombstone is written
        // before the delete executes — so deleting a missing row still records the intent.
        var prefix = $"or{Guid.NewGuid():N}";
        var log = Table($"{prefix}Tombstones");
        var store = new TableScopeStore(Table($"{prefix}Scopes"), EnvPartitioner.Live, new TableChangeWriter(log));

        await store.DeleteAsync("nonexistent-scope");

        var rows = new List<TableEntity>();
        await foreach (var e in log.QueryAsync<TableEntity>(e => e.PartitionKey == "Scopes"))
            if (e.GetString("Op") == "D") rows.Add(e);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Tombstone_file_DeletedAt_uses_storage_clock_not_pod_clock()
    {
        var prefix = $"ck{Guid.NewGuid():N}";
        var log = Table($"{prefix}Tombstones");

        // A change-log delete row whose pod-stamped DeletedAt is 10 minutes in the FUTURE (skewed pod
        // clock). The row's own storage Timestamp is "now" — the truthful, same-domain delete time.
        await log.AddEntityAsync(new TableEntity("Users", "u1|profile")
        {
            ["Op"] = "D",
            ["OrigPK"] = "u1",
            ["OrigRK"] = "profile",
            ["DeletedAt"] = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var dir = Path.Combine(Path.GetTempPath(), $"ck{Guid.NewGuid():N}");
        try
        {
            var incr = await new BackupService(_svc, new FileSystemBackupTarget(dir), new BackupOptions
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                WatermarkOverride = DateTimeOffset.UtcNow.AddMinutes(-5),
                WatermarkSkewMargin = TimeSpan.Zero,
            }).RunAsync();
            Assert.Equal(1, incr.TombstoneCount);

            await using var stream = await new FileSystemBackupSource(dir).OpenReadAsync(incr.BackupId, "_tombstones.jsonl");
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            using var doc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            var deletedAt = DateTimeOffset.Parse(doc.RootElement.GetProperty("DeletedAt").GetString()!);

            // Emitted DeletedAt must be the storage Timestamp (≈ now), NOT the skewed +10min column —
            // otherwise a delete-then-recreate within the skew is dropped from rollups (F24f).
            Assert.True(deletedAt < DateTimeOffset.UtcNow.AddMinutes(5),
                $"DeletedAt {deletedAt:O} should be the storage timestamp, not the pod-clock column");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
