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
/// Increment 2 acceptance: a change-log-driven incremental backup produces the exact same result as a
/// Timestamp scan. For each change-logged table, the set of entity keys captured by reading the change-log
/// (Op="U" + point-read) must equal the set a full scan would capture, and the tombstone (delete) file must
/// match too. Run tokenized/encrypted, as production does — so index keys are '|'-free hex tokens and the
/// change-log RK ("{pk}|{rk}") splits unambiguously. Azurite.
/// </summary>
[Collection("Azurite")]
public class ChangeLogBackupEquivalenceTests(AzuriteFixture azurite)
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

    private TableUserStore NewStore(string prefix, EnvPartitioner? env = null)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(
            T("Users"), T("UserEmails"), T("UserLogins"), T("UserExternalIds"), T("UserFirstNames"), T("UserLastNames"),
            env ?? EnvPartitioner.Live, tombstoneWriter: new TableChangeWriter(T("Tombstones")),
            fieldCipher: new FakeCipher(), indexTokenizer: new FakeTokenizer(),
            userEmailDomainsTable: T("UserEmailDomains"), userEmailLocalPrefixesTable: T("UserEmailLocalPrefixes"));
    }

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

    private static async Task<List<(string Table, string Pk, string Rk)>> TombstonesAsync(IBackupSource src, string backupId)
    {
        var list = new List<(string Table, string Pk, string Rk)>();
        await using var stream = await src.OpenReadAsync(backupId, "_tombstones.jsonl");
        if (stream is null) return list;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            list.Add((r.GetProperty("Table").GetString()!, r.GetProperty("PartitionKey").GetString()!, r.GetProperty("RowKey").GetString()!));
        }
        return list;
    }

    [Fact]
    public async Task Changelog_incremental_matches_scan_incremental()
    {
        var prefix = $"eq{Guid.NewGuid():N}";
        var store = NewStore(prefix);

        // Seed (before the watermark)
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.CreateAsync(User("u2", "grace@acme.test", "Grace", "Hopper"));
        await store.CreateAsync(User("u3", "edsger@acme.test", "Edsger", "Dijkstra"));
        await store.SetExternalIdAsync("u1", "client1", "ext-ada");

        // Watermark an hour in the past so both read paths capture the full current state of the change-
        // logged tables — deterministic, with no dependence on app-vs-storage clock skew at a boundary instant.
        var watermark = DateTimeOffset.UtcNow.AddHours(-1);

        // Mutations — at least one surviving upsert per change-logged table, plus a
        // delete (u3) and an upsert-then-delete within the window (u3's login) to exercise the collapse.
        await store.UpdateAsync(User("u1", "ada2@acme.test", "Ada", "Lovelace"));                        // UserEmails, Users
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "google", ProviderKey = "ada@gmail" }); // UserLogins (survives)
        await store.SetExternalIdAsync("u2", "client1", "ext-grace");                                    // UserExternalIds
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u3", Provider = "saml", ProviderKey = "edsger@idp" }); // logged then deleted
        await store.DeleteAsync("u3");                                                                   // tombstones
        await store.CreateAsync(User("u4", "alan@acme.test", "Alan", "Turing"));                         // UserEmails, names

        var scanDir = Path.Combine(Path.GetTempPath(), $"scan{Guid.NewGuid():N}");
        var logDir = Path.Combine(Path.GetTempPath(), $"log{Guid.NewGuid():N}");
        try
        {
            var scanTarget = new FileSystemBackupTarget(scanDir);
            var logTarget = new FileSystemBackupTarget(logDir);
            // Same watermark for both; safely in the past so each captures the full current state.
            await scanTarget.SetLastWatermarkAsync(watermark);
            await logTarget.SetLastWatermarkAsync(watermark);

            BackupOptions Opt(IReadOnlySet<string>? changeLogged) => new()
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                ChangeLoggedTables = changeLogged,
            };

            // Empty set => everything scans (baseline). The eligible set => the 5 tables read the change-log.
            var scan = await new BackupService(_svc, scanTarget, Opt(new HashSet<string>())).RunAsync();
            var log = await new BackupService(_svc, logTarget, Opt(BackupDefaults.ChangeLoggedTables)).RunAsync();

            var scanSrc = new FileSystemBackupSource(scanDir);
            var logSrc = new FileSystemBackupSource(logDir);

            foreach (var table in BackupDefaults.ChangeLoggedTables)
            {
                var scanned = await KeysAsync(scanSrc, scan.BackupId, table);
                var logged = await KeysAsync(logSrc, log.BackupId, table);
                Assert.True(scanned.SetEquals(logged),
                    $"{table}: scan=[{string.Join(",", scanned.Order())}] changelog=[{string.Join(",", logged.Order())}]");
                Assert.NotEmpty(logged); // sanity: this table actually had a captured change
            }

            // Deletes come from the same tombstone pass in both modes — must be identical.
            var scanTomb = await KeysAsync(scanSrc, scan.BackupId, "_tombstones");
            var logTomb = await KeysAsync(logSrc, log.BackupId, "_tombstones");
            Assert.True(scanTomb.SetEquals(logTomb));
            Assert.NotEmpty(logTomb); // u3's deletes
        }
        finally
        {
            if (Directory.Exists(scanDir)) Directory.Delete(scanDir, true);
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
        }
    }

    [Fact]
    public async Task Changelog_incremental_handles_env_prefixed_keys()
    {
        // Sandbox env ⇒ every PartitionKey is {env}|{natural} (EnvPartitioner), so the change-log composite
        // RK is {env}|{natural}|{rk} and a naive split-on-first-'|' recovers the WRONG (pk, rk): the upsert
        // point-read 404s and the row silently vanishes, and delete tombstones carry a mangled PK that removes
        // the wrong key on restore. Regression guard for F24d — the OrigPK/OrigRK columns fix both. (This is
        // the case tokenization can't dodge: the env prefix puts a '|' ahead of the '|'-free hex token.)
        var prefix = $"env{Guid.NewGuid():N}";
        var store = NewStore(prefix, new EnvPartitioner("staging"));

        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.CreateAsync(User("u2", "grace@acme.test", "Grace", "Hopper"));
        await store.CreateAsync(User("u3", "edsger@acme.test", "Edsger", "Dijkstra"));
        await store.SetExternalIdAsync("u1", "client1", "ext-ada");

        var watermark = DateTimeOffset.UtcNow.AddHours(-1);

        await store.UpdateAsync(User("u1", "ada2@acme.test", "Ada", "Lovelace"));
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "google", ProviderKey = "ada@gmail" });
        await store.SetExternalIdAsync("u2", "client1", "ext-grace");
        await store.DeleteAsync("u3");
        await store.CreateAsync(User("u4", "alan@acme.test", "Alan", "Turing"));

        var scanDir = Path.Combine(Path.GetTempPath(), $"scan{Guid.NewGuid():N}");
        var logDir = Path.Combine(Path.GetTempPath(), $"log{Guid.NewGuid():N}");
        try
        {
            var scanTarget = new FileSystemBackupTarget(scanDir);
            var logTarget = new FileSystemBackupTarget(logDir);
            await scanTarget.SetLastWatermarkAsync(watermark);
            await logTarget.SetLastWatermarkAsync(watermark);

            BackupOptions Opt(IReadOnlySet<string>? changeLogged) => new()
            {
                TablePrefix = prefix,
                Incremental = true,
                Gzip = false,
                ChangeLoggedTables = changeLogged,
            };

            var scan = await new BackupService(_svc, scanTarget, Opt(new HashSet<string>())).RunAsync();
            var log = await new BackupService(_svc, logTarget, Opt(BackupDefaults.ChangeLoggedTables)).RunAsync();

            var scanSrc = new FileSystemBackupSource(scanDir);
            var logSrc = new FileSystemBackupSource(logDir);

            // Upsert equivalence: the change-log incremental must capture the same env-prefixed keys as a scan.
            // Pre-fix, the mis-split point-read 404s and `logged` comes back empty for these tables.
            foreach (var table in BackupDefaults.ChangeLoggedTables)
            {
                var scanned = await KeysAsync(scanSrc, scan.BackupId, table);
                var logged = await KeysAsync(logSrc, log.BackupId, table);
                Assert.True(scanned.SetEquals(logged),
                    $"{table}: scan=[{string.Join(",", scanned.Order())}] changelog=[{string.Join(",", logged.Order())}]");
                Assert.NotEmpty(logged);
            }

            // Every key here is env-prefixed, so each tombstone's PartitionKey must be "staging|…". Pre-fix the
            // split mangles it to exactly "staging" — assert on the SEPARATE PK (a "{pk}|{rk}" concat can't tell
            // them apart). This covers the tombstone pass, which the scan-equivalence check can't (both modes
            // share it, so both would be wrong identically).
            var tombs = await TombstonesAsync(logSrc, log.BackupId);
            Assert.NotEmpty(tombs); // u3's deletes + u1's replaced index rows
            Assert.All(tombs, t => Assert.StartsWith("staging|", t.Pk));
        }
        finally
        {
            if (Directory.Exists(scanDir)) Directory.Delete(scanDir, true);
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
        }
    }
}
