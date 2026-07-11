using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Backup;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Increment 3 acceptance: the periodic full-scan backstop. Change-log incrementals only see rows the
/// store captured, so login-state writes (deliberately uncaptured) and rows written outside the store
/// (pre-capture pods on a deploy, raw-table maintenance sweeps) are invisible to them. A backstop run
/// (WatermarkOverride = last full-coverage scan, change-log path off) re-scans the whole window on the
/// Timestamp column and picks them up. Also covers the MergeService delete-then-recreate fix: a pooled
/// tombstone must not remove a row whose captured Timestamp postdates the delete. Azurite.
/// </summary>
[Collection("Azurite")]
public class ScanBackstopTests(AzuriteFixture azurite)
{
    private sealed class FakeCipher : IFieldCipher
    {
        public const string Prefix = "enc:";
        public Task<string> ProtectAsync(string p, CancellationToken ct = default)
            => Task.FromResult(Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(p)));
        public Task<string> ResolveAsync(string s, CancellationToken ct = default)
            => Task.FromResult(s.StartsWith(Prefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(s[Prefix.Length..])) : s);
    }

    private sealed class FakeTokenizer : IIndexTokenizer
    {
        public static string Token(string v) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v)));
        public Task<string> TokenizeAsync(string value, CancellationToken ct = default) => Task.FromResult(Token(value));
        public Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(values.Select(Token).ToList());
    }

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient Table(string prefix, string name)
    {
        var c = _svc.GetTableClient($"{prefix}{name}");
        c.CreateIfNotExists();
        return c;
    }

    private TableUserStore NewStore(string prefix) => new(
        Table(prefix, "Users"), Table(prefix, "UserEmails"), Table(prefix, "UserLogins"),
        Table(prefix, "UserExternalIds"), Table(prefix, "UserFirstNames"), Table(prefix, "UserLastNames"),
        EnvPartitioner.Live, tombstoneWriter: new TableChangeWriter(Table(prefix, "Tombstones")),
        fieldCipher: new FakeCipher(), indexTokenizer: new FakeTokenizer(),
        userEmailDomainsTable: Table(prefix, "UserEmailDomains"), userEmailLocalPrefixesTable: Table(prefix, "UserEmailLocalPrefixes"));

    private static AuthUser User(string id, string email, string first, string last) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
    };

    private static async Task<HashSet<string>> KeysAsync(IBackupSource src, string backupId, string fileBase)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var stream = await src.OpenReadAsync(backupId, $"{fileBase}.jsonl");
        if (stream is null) return set;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var pk = doc.RootElement.GetProperty("PartitionKey").GetString();
            var rk = doc.RootElement.GetProperty("RowKey").GetString();
            set.Add($"{pk}|{rk}");
        }
        return set;
    }

    [Fact]
    public async Task Backstop_catches_login_state_and_uncaptured_writes()
    {
        var prefix = $"bs{Guid.NewGuid():N}";
        var store = NewStore(prefix);

        // Seed strictly BEFORE the watermark, mutations strictly after — the delays keep the storage-
        // assigned Timestamps clear of the boundary (Azurite runs on this machine's clock).
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.CreateAsync(User("u2", "grace@acme.test", "Grace", "Hopper"));
        await Task.Delay(250);
        var watermark = DateTimeOffset.UtcNow;
        await Task.Delay(250);

        // Two writes the change-log never sees:
        // 1) a login-state merge write on the Users row (deliberately uncaptured hot-path);
        await store.RecordFailedLoginAsync("u1", maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        // 2) a row written straight to the table, bypassing the store (pre-capture pod / raw-table sweep).
        var rogueKey = FakeTokenizer.Token("ROGUE@ACME.TEST");
        await Table(prefix, "UserEmails").UpsertEntityAsync(
            new TableEntity(rogueKey, "lookup") { ["UserId"] = "u9" }, TableUpdateMode.Replace);

        var logDir = Path.Combine(Path.GetTempPath(), $"cl{Guid.NewGuid():N}");
        var backstopDir = Path.Combine(Path.GetTempPath(), $"bs{Guid.NewGuid():N}");
        try
        {
            var logTarget = new FileSystemBackupTarget(logDir);
            await logTarget.SetLastWatermarkAsync(watermark);

            // Change-log incremental (Users included, the flip's configuration): both writes invisible.
            // Margin zeroed: this test asserts exact window boundaries with sub-second seed/watermark
            // spacing; the production 5-min skew margin would (correctly) pull the seeds back in.
            var log = await new BackupService(_svc, logTarget, new BackupOptions
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                ChangeLoggedTables = BackupDefaults.ChangeLoggedTablesWithUsers,
                WatermarkSkewMargin = TimeSpan.Zero,
            }).RunAsync();

            var logSrc = new FileSystemBackupSource(logDir);
            var logUsers = await KeysAsync(logSrc, log.BackupId, "Users");
            var logEmails = await KeysAsync(logSrc, log.BackupId, "UserEmails");
            Assert.DoesNotContain("u1|profile", logUsers);
            Assert.DoesNotContain($"{rogueKey}|lookup", logEmails);
            // The manifest records the change-log reads (all 6 tables were eligible; only those with
            // captured changes in the window appear as files, but the read PATH is per-table).
            Assert.NotNull(log.ChangeLogTables);
            Assert.Contains("Users", log.ChangeLogTables);

            // Backstop: scan-path incremental over the whole window since the last full-coverage scan.
            var backstopTarget = new FileSystemBackupTarget(backstopDir);
            await backstopTarget.SetLastWatermarkAsync(DateTimeOffset.UtcNow); // stored watermark is IGNORED
            var backstop = await new BackupService(_svc, backstopTarget, new BackupOptions
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                WatermarkOverride = watermark,
                WatermarkSkewMargin = TimeSpan.Zero,
            }).RunAsync();

            var bsSrc = new FileSystemBackupSource(backstopDir);
            var bsUsers = await KeysAsync(bsSrc, backstop.BackupId, "Users");
            var bsEmails = await KeysAsync(bsSrc, backstop.BackupId, "UserEmails");
            Assert.Contains("u1|profile", bsUsers);                 // login-state write caught
            Assert.Contains($"{rogueKey}|lookup", bsEmails);         // uncaptured writer caught
            Assert.DoesNotContain("u2|profile", bsUsers);           // untouched row NOT re-captured
            Assert.Null(backstop.ChangeLogTables);                  // manifest: full scan coverage
            Assert.Equal(watermark, backstop.Watermark);            // override actually used
        }
        finally
        {
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
            if (Directory.Exists(backstopDir)) Directory.Delete(backstopDir, true);
        }
    }

    [Fact]
    public async Task Merge_keeps_row_recreated_after_delete()
    {
        var prefix = $"rc{Guid.NewGuid():N}";
        var store = NewStore(prefix);

        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.CreateAsync(User("u2", "grace@acme.test", "Grace", "Hopper"));

        var dir = Path.Combine(Path.GetTempPath(), $"mg{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            BackupOptions Opt(bool incremental) => new()
            {
                TablePrefix = prefix,
                Incremental = incremental,
                Gzip = false,
            };

            var full = await new BackupService(_svc, target, Opt(false)).RunAsync();

            // Window event 1: delete both users (tombstones with DeletedAt).
            await store.DeleteAsync("u1");
            await store.DeleteAsync("u2");
            var incr1 = await new BackupService(_svc, target, Opt(true)).RunAsync();

            // Window event 2: u1 comes back with the same id + email (same PK|RK everywhere);
            // u2 stays deleted. The delay guarantees the recreated row's storage Timestamp strictly
            // postdates the tombstone's client-stamped DeletedAt.
            await Task.Delay(250);
            await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
            var incr2 = await new BackupService(_svc, target, Opt(true)).RunAsync();

            var source = new FileSystemBackupSource(dir);
            var merged = await new MergeService(source).MergeToTargetAsync(
                full.BackupId, [incr1.BackupId, incr2.BackupId], target, gzip: false);

            var users = await KeysAsync(source, merged.BackupId, "Users");
            var emails = await KeysAsync(source, merged.BackupId, "UserEmails");

            // Recreated row survives the pooled tombstone; the stayed-deleted one doesn't.
            Assert.Contains("u1|profile", users);
            Assert.DoesNotContain("u2|profile", users);
            Assert.Contains($"{FakeTokenizer.Token("ADA@ACME.TEST")}|lookup", emails);
            Assert.DoesNotContain($"{FakeTokenizer.Token("GRACE@ACME.TEST")}|lookup", emails);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
