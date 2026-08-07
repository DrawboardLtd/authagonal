using Authagonal.Backup;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Three backup options whose documented use produced an outcome the operator was not told about.
/// </summary>
[Collection("Azurite")]
public class BackupOptionAsymmetryTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    // ── #54: the watermark was one value per output directory ────────────────

    /// <summary>
    /// A second tenant's FIRST incremental into a shared output directory must take a full backup.
    /// </summary>
    /// <remarks>
    /// <c>--prefix</c> exists for multi-tenancy, and the watermark was a single <c>.lastbackup</c> per output
    /// directory advanced by any successful run. So hourly <c>--prefix tenantA --incremental</c> left a recent
    /// watermark, and tenant B's first ever run found it, skipped the full-backup fallback, and produced an
    /// archive covering only the last hour — a first backup that silently contains almost nothing.
    /// </remarks>
    [Fact]
    public async Task ADifferentPrefixDoesNotInheritAnotherPrefixesWatermark()
    {
        var dir = NewDir();
        var target = new FileSystemBackupTarget(dir);

        var tenantA = new BackupOptions { TablePrefix = "tenantA", Incremental = true };
        var tenantB = new BackupOptions { TablePrefix = "tenantB", Incremental = true };

        Assert.NotEqual(tenantA.WatermarkScope(), tenantB.WatermarkScope());

        await target.SetLastWatermarkAsync(DateTimeOffset.UtcNow, scope: tenantA.WatermarkScope());

        Assert.NotNull(await target.GetLastWatermarkAsync(scope: tenantA.WatermarkScope()));
        Assert.Null(await target.GetLastWatermarkAsync(scope: tenantB.WatermarkScope()));
    }

    /// <summary>A run restricted to a subset of tables owns its own watermark.</summary>
    [Fact]
    public async Task ASubsetRunDoesNotAdvanceTheFullSetsWatermark()
    {
        var dir = NewDir();
        var target = new FileSystemBackupTarget(dir);

        var subset = new BackupOptions { Tables = ["Users", "Clients"], Incremental = true };
        var fullSet = new BackupOptions { Incremental = true };

        await target.SetLastWatermarkAsync(DateTimeOffset.UtcNow, scope: subset.WatermarkScope());

        Assert.NotNull(await target.GetLastWatermarkAsync(scope: subset.WatermarkScope()));
        Assert.Null(await target.GetLastWatermarkAsync(scope: fullSet.WatermarkScope()));
    }

    /// <summary>Table order must not change the identity of a run.</summary>
    [Fact]
    public void TheScopeIsIndependentOfTableOrder()
    {
        var a = new BackupOptions { Tables = ["Users", "Clients"] };
        var b = new BackupOptions { Tables = ["Clients", "Users"] };

        Assert.Equal(a.WatermarkScope(), b.WatermarkScope());
    }

    /// <summary>A plain schedule keeps the unscoped file, so an upgrade does not force a full backup.</summary>
    [Fact]
    public async Task AnUnprefixedFullSetRunKeepsUsingTheUnscopedWatermark()
    {
        var dir = NewDir();
        var target = new FileSystemBackupTarget(dir);
        var plain = new BackupOptions { Incremental = true };

        Assert.Null(plain.WatermarkScope());

        await target.SetLastWatermarkAsync(DateTimeOffset.UtcNow, scope: plain.WatermarkScope());
        Assert.True(File.Exists(Path.Combine(dir, ".lastbackup")));
    }

    /// <summary>
    /// A scoped run with no watermark of its own falls back to the unscoped one.
    /// </summary>
    /// <remarks>
    /// So a deployment that upgrades mid-schedule does not take one full backup per scope — correct, but
    /// surprising. A scope that has genuinely never run reads null from both, which is the multi-tenant case.
    /// </remarks>
    [Fact]
    public async Task AScopedRunFallsBackToTheUnscopedWatermarkOnUpgrade()
    {
        var dir = NewDir();
        var target = new FileSystemBackupTarget(dir);
        var legacy = DateTimeOffset.UtcNow.AddHours(-1);

        await target.SetLastWatermarkAsync(legacy, scope: null);

        var scoped = new BackupOptions { TablePrefix = "tenantA", Incremental = true };
        var read = await target.GetLastWatermarkAsync(scope: scoped.WatermarkScope());

        Assert.NotNull(read);
        Assert.Equal(legacy.ToUnixTimeSeconds(), read!.Value.ToUnixTimeSeconds());
    }

    // ── #55: an archive that could never be restored ─────────────────────────

    /// <summary>
    /// Backing up a table the restore allowlist refuses is refused up front.
    /// </summary>
    /// <remarks>
    /// Backup accepted any <c>--tables</c> value and wrote, hashed and manifest-signed the result, while
    /// <c>RestoreService</c>'s allowlist is <c>BackupDefaults.Tables</c> — and it throws before the
    /// <c>--tables</c> filter, mid-loop. So <c>--tables Users,Clients,RevokedTokens</c> produced a complete
    /// signed archive that could never be restored, and the failure surfaced during the restore, which is the
    /// worst moment to find out.
    /// </remarks>
    [Fact]
    public async Task BackingUpATableTheRestoreWouldRefuseIsRefusedUpFront()
    {
        var dir = NewDir();
        var options = new BackupOptions
        {
            Tables = ["Users", "Clients", "RevokedTokens"],
            Gzip = false,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BackupService(_svc, new FileSystemBackupTarget(dir), options).RunAsync());

        Assert.Contains("RevokedTokens", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be restored", ex.Message, StringComparison.Ordinal);

        // And nothing was written, so there is no half-archive to mistake for a good one.
        Assert.False(Directory.Exists(dir) && Directory.GetDirectories(dir).Length > 0);
    }

    /// <summary>
    /// A host that keeps its own tables beside Authagonal's declares them, and both halves accept them.
    /// </summary>
    /// <remarks>
    /// The guard's rule is that the table set is declared ahead of time rather than taken from the archive.
    /// Pinning that declaration to <c>BackupDefaults.Tables</c> made it "whatever this library ships", which
    /// is a different rule and a wrong one for the library's own principal consumer: Authagonal Cloud carries
    /// a support desk, an audit family and billing counters in the same per-tenant table set, so the
    /// hardcoded list refused every backup it took and every archive it had already written. Declaring a
    /// wider universe keeps the property — the set is still fixed, still the caller's, still never the
    /// archive's.
    /// </remarks>
    [Fact]
    public async Task AHostDeclaresItsOwnTableUniverse()
    {
        var dir = NewDir();
        string[] hostTables = [.. BackupDefaults.Tables, "SupportTickets", "AuditLog"];

        // Refused without the declaration...
        var undeclared = new BackupOptions
        {
            Tables = ["Users", "SupportTickets"],
            TablePrefix = $"bk{Guid.NewGuid():N}"[..12],
            Gzip = false,
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BackupService(_svc, new FileSystemBackupTarget(dir), undeclared).RunAsync());
        Assert.Contains("SupportTickets", ex.Message, StringComparison.Ordinal);

        // ...accepted with it.
        var declared = new BackupOptions
        {
            Tables = ["Users", "SupportTickets"],
            KnownTables = hostTables,
            TablePrefix = $"bk{Guid.NewGuid():N}"[..12],
            Gzip = false,
        };
        var manifest = await new BackupService(_svc, new FileSystemBackupTarget(dir), declared).RunAsync();
        Assert.NotNull(manifest);

        // And a table outside even the widened set is still refused, so the declaration is a bound and not
        // an escape hatch.
        var beyond = new BackupOptions
        {
            Tables = ["Users", "RevokedTokens"],
            KnownTables = hostTables,
            TablePrefix = $"bk{Guid.NewGuid():N}"[..12],
            Gzip = false,
        };
        var stillRefused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BackupService(_svc, new FileSystemBackupTarget(NewDir()), beyond).RunAsync());
        Assert.Contains("RevokedTokens", stillRefused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restore holds the archive to the host's declared set, not to this library's.
    /// </summary>
    /// <remarks>
    /// The two halves have to agree: a backup written under a wider declaration is only restorable through a
    /// restore that declares the same universe, and an archive naming a table outside it is still refused
    /// rather than creating the table.
    /// </remarks>
    [Fact]
    public async Task RestoreHoldsTheArchiveToTheHostsDeclaredSet()
    {
        var dir = NewDir();
        var prefix = $"bk{Guid.NewGuid():N}"[..12];
        string[] hostTables = [.. BackupDefaults.Tables, "SupportTickets"];

        // A tenant row in a host-owned table, backed up under the host's declaration.
        var supportTable = _svc.GetTableClient(prefix + "SupportTickets");
        await supportTable.CreateIfNotExistsAsync();
        await supportTable.UpsertEntityAsync(new TableEntity("t1", "ticket") { ["Subject"] = "printer on fire" });

        var target = new FileSystemBackupTarget(dir);
        var manifest = await new BackupService(_svc, target, new BackupOptions
        {
            Tables = ["SupportTickets"],
            KnownTables = hostTables,
            TablePrefix = prefix,
            Gzip = false,
        }).RunAsync();

        var source = new FileSystemBackupSource(dir);

        // Undeclared: the archive is refused rather than restored. (AllowUnauthenticatedManifest only gets
        // past the manifest-key guard, which would otherwise refuse first and hide what is being tested.)
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RestoreService(_svc, source, new RestoreOptions
            {
                TablePrefix = $"rs{Guid.NewGuid():N}"[..12],
                AllowUnauthenticatedManifest = true,
            }).RunAsync(manifest.BackupId));
        Assert.Contains("SupportTickets", refused.Message, StringComparison.Ordinal);

        // Declared: it restores.
        var restorePrefix = $"rs{Guid.NewGuid():N}"[..12];
        var result = await new RestoreService(_svc, source, new RestoreOptions
        {
            TablePrefix = restorePrefix,
            KnownTables = hostTables,
            AllowUnauthenticatedManifest = true,
        }).RunAsync(manifest.BackupId);

        Assert.Equal(0, result.TotalErrors);
        var restored = await _svc.GetTableClient(restorePrefix + "SupportTickets")
            .GetEntityAsync<TableEntity>("t1", "ticket");
        Assert.Equal("printer on fire", restored.Value["Subject"]);
    }

    /// <summary>The control: the real table set is accepted.</summary>
    [Fact]
    public async Task BackingUpKnownTablesIsAccepted()
    {
        var dir = NewDir();
        var options = new BackupOptions
        {
            Tables = ["Users", "Clients"],
            TablePrefix = $"bk{Guid.NewGuid():N}"[..12],
            Gzip = false,
        };

        var manifest = await new BackupService(_svc, new FileSystemBackupTarget(dir), options).RunAsync();
        Assert.NotNull(manifest);
    }

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), $"bkopt{Guid.NewGuid():N}");
}
