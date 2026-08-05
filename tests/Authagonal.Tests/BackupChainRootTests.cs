using Authagonal.Backup;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// <c>BackupManifest.ParentBackupId</c> was serialized into every manifest, covered by the manifest MAC,
/// read on the recovery path — and written by nothing.
/// </summary>
/// <remarks>
/// So it was always null, and the one message designed to tell an operator which archive is the chain
/// root rendered as "Restore the parent full ('') with --mode clean" — omitting the only fact it exists
/// to supply, at the moment they are recovering. More broadly,
/// <c>docs/backup-restore.md</c>'s required sequence ("a full backup followed by incrementals, oldest
/// first") was enforced by nothing, because an incremental carried no reference to its full.
/// </remarks>
[Collection("Azurite")]
public class BackupChainRootTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    [Fact]
    public async Task AnIncrementalNamesTheFullItChainsFrom()
    {
        var (dir, prefix) = await NewScopeAsync();
        try
        {
            var full = await RunAsync(dir, prefix, incremental: false);
            Assert.Equal("full", full.Mode);
            // A full backup IS the chain root; it does not have one.
            Assert.Null(full.ParentBackupId);

            var incr = await RunAsync(dir, prefix, incremental: true);

            Assert.Equal("incremental", incr.Mode);
            Assert.Equal(full.BackupId, incr.ParentBackupId);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>A second full re-roots the chain, so later incrementals name the newer one.</summary>
    [Fact]
    public async Task AFullBackupBecomesTheChainRootForEverythingAfterIt()
    {
        var (dir, prefix) = await NewScopeAsync();
        try
        {
            var first = await RunAsync(dir, prefix, incremental: false);
            var afterFirst = await RunAsync(dir, prefix, incremental: true);
            Assert.Equal(first.BackupId, afterFirst.ParentBackupId);

            // backupId is "yyyyMMdd-HHmmss" — one-second resolution, so two runs inside the same second
            // share an id (and, since the prefix is not part of the output path, a directory). Waiting
            // keeps this test about re-rooting rather than about that.
            await Task.Delay(1100);
            var second = await RunAsync(dir, prefix, incremental: false);
            var afterSecond = await RunAsync(dir, prefix, incremental: true);

            Assert.NotEqual(first.BackupId, second.BackupId);
            Assert.Equal(second.BackupId, afterSecond.ParentBackupId);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The chain root is per scope, exactly as the watermark is.
    /// </summary>
    /// <remarks>
    /// One value per output directory would name tenant A's full as tenant B's parent — the same defect
    /// the watermark's <c>scope</c> parameter was added for, and for the same documented reason: the
    /// stated purpose of <c>--prefix</c> is running more than one tenant into one target.
    /// </remarks>
    [Fact]
    public async Task TheChainRootIsScopedPerPrefix()
    {
        var (dir, prefixA) = await NewScopeAsync();
        var prefixB = $"tb{Guid.NewGuid():N}"[..12];
        await CreateTablesAsync(prefixB);
        try
        {
            var fullA = await RunAsync(dir, prefixA, incremental: false);
            await Task.Delay(1100); // distinct backupId — see the note in the re-rooting test
            var fullB = await RunAsync(dir, prefixB, incremental: false);

            var incrA = await RunAsync(dir, prefixA, incremental: true);

            Assert.Equal(fullA.BackupId, incrA.ParentBackupId);
            Assert.NotEqual(fullB.BackupId, incrA.ParentBackupId);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A dry run records nothing, so it cannot become the parent of archives it did not produce.
    /// </summary>
    [Fact]
    public async Task ADryRunDoesNotBecomeTheChainRoot()
    {
        var (dir, prefix) = await NewScopeAsync();
        try
        {
            var real = await RunAsync(dir, prefix, incremental: false);
            await Task.Delay(1100); // distinct backupId — see the note in the re-rooting test
            var dry = await RunAsync(dir, prefix, incremental: false, dryRun: true);

            var incr = await RunAsync(dir, prefix, incremental: true);

            Assert.NotEqual(dry.BackupId, incr.ParentBackupId);
            Assert.Equal(real.BackupId, incr.ParentBackupId);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// An incremental in a scope that has never taken a full names no parent rather than a wrong one.
    /// </summary>
    [Fact]
    public async Task WithNoRecordedFullTheParentIsNullRatherThanWrong()
    {
        var (dir, prefix) = await NewScopeAsync();
        try
        {
            // WatermarkOverride makes this an incremental without a preceding full in this scope.
            var incr = await RunAsync(dir, prefix, incremental: true);

            Assert.Equal("incremental", incr.Mode);
            Assert.Null(incr.ParentBackupId);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>The target reads back what it recorded, per scope, with the documented fallback.</summary>
    [Fact]
    public async Task TheTargetRoundTripsTheChainRootAndFallsBackToTheUnscopedValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tb{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);

            Assert.Null(await target.GetLastFullBackupIdAsync());

            await target.SetLastFullBackupIdAsync("20260804-030000");
            Assert.Equal("20260804-030000", await target.GetLastFullBackupIdAsync());

            // A scope with nothing of its own reads the unscoped value, so a deployment upgrading
            // mid-schedule keeps naming a parent instead of reverting to "unknown" — the same fallback
            // the watermark has, for the same reason.
            Assert.Equal("20260804-030000", await target.GetLastFullBackupIdAsync(scope: "tenantA"));

            await target.SetLastFullBackupIdAsync("20260805-030000", scope: "tenantA");
            Assert.Equal("20260805-030000", await target.GetLastFullBackupIdAsync(scope: "tenantA"));
            Assert.Equal("20260804-030000", await target.GetLastFullBackupIdAsync());
        }
        finally { Cleanup(dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(string Dir, string Prefix)> NewScopeAsync()
    {
        var prefix = $"tb{Guid.NewGuid():N}"[..12];
        await CreateTablesAsync(prefix);
        return (Path.Combine(Path.GetTempPath(), $"tb{Guid.NewGuid():N}"), prefix);
    }

    private async Task CreateTablesAsync(string prefix)
    {
        foreach (var table in BackupDefaults.Tables)
            await _svc.GetTableClient(prefix + table).CreateIfNotExistsAsync();
    }

    private Task<BackupManifest> RunAsync(
        string dir, string prefix, bool incremental, bool dryRun = false)
        => new BackupService(_svc, new FileSystemBackupTarget(dir), new BackupOptions
        {
            TablePrefix = prefix,
            Incremental = incremental,
            Gzip = false,
            DryRun = dryRun,
            // A fixed override rather than the stored watermark: the point under test is the chain root,
            // and this keeps the run an incremental regardless of what any sibling run recorded.
            WatermarkOverride = incremental ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
            WatermarkSkewMargin = TimeSpan.Zero,
        }).RunAsync();

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }
}
