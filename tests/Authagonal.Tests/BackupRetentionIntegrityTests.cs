using System.Text.Json;
using Authagonal.Backup;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// The retention path: what a rollup is allowed to read, what it must sign, and which deletes a scoped restore
/// is allowed to execute.
/// </summary>
/// <remarks>
/// Three defects with one shape — a check that existed on one path and not its sibling.
/// <list type="bullet">
/// <item><c>RestoreService</c> learned to scope the data apply to <c>CleanEnvPrefix</c>, and the tombstone
/// apply was left unscoped. Deletes are the more destructive half.</item>
/// <item><c>BackupService</c> signs its manifest; <c>MergeService</c> did not, so the retained copy was the one
/// that could not be authenticated — and <c>RollupAndCleanAsync</c> deletes the signed originals.</item>
/// <item><c>RestoreService</c> verifies its inputs against <c>FileHashes</c>; <c>MergeService</c> verified
/// nothing and hashed its output fresh, so the retention job laundered a tampered archive into an
/// authenticated one.</item>
/// </list>
/// </remarks>
[Collection("Azurite")]
public class BackupRetentionIntegrityTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient Table(string name)
    {
        var c = _svc.GetTableClient(name);
        c.CreateIfNotExists();
        return c;
    }

    private static byte[] Key(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    // ── tombstone deletes are scoped to the env being restored ───────────────────────────────────────

    /// <summary>
    /// Restoring one env must not execute a sibling env's recorded deletes.
    /// </summary>
    /// <remarks>
    /// <c>BackupTombstonesAsync</c> filters the change log on <c>Timestamp</c> alone — <c>BackupOptions</c> has
    /// no env filter — so on a shared sandbox table set the <c>_tombstones</c> file holds every env's deletes,
    /// with the <c>{env}|</c> prefix intact in the <c>OrigPK</c> column it writes as <c>PartitionKey</c>. The
    /// sibling row is created AFTER the backup, so it is not in the archive: nothing could restore it, which is
    /// what made this unrecoverable rather than merely wrong.
    /// </remarks>
    [Fact]
    public async Task A_scoped_restore_does_not_apply_another_envs_tombstones()
    {
        var prefix = $"tb{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        var log = Table($"{prefix}Tombstones");

        await users.AddEntityAsync(new TableEntity("sandbox-1|u1", "profile") { ["Email"] = "one@example.com" });

        // Two recorded deletes in the window: one in the env being restored, one in a sibling.
        foreach (var pk in new[] { "sandbox-1|gone", "sandbox-2|alive" })
        {
            await log.AddEntityAsync(new TableEntity("Users", $"{pk}|profile")
            {
                ["Op"] = "D",
                ["OrigPK"] = pk,
                ["OrigRK"] = "profile",
                ["DeletedAt"] = DateTimeOffset.UtcNow,
            });
        }

        var dir = Path.Combine(Path.GetTempPath(), $"tb{Guid.NewGuid():N}");
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
            Assert.Equal(2, incr.TombstoneCount);

            // The sibling's row is created after the backup — so it exists only in the live table.
            await users.AddEntityAsync(
                new TableEntity("sandbox-2|alive", "profile") { ["Email"] = "sibling@example.com" });

            var result = await new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                AllowUnauthenticatedManifest = true,
                TablePrefix = prefix,
                Mode = RestoreMode.Upsert,
                CleanEnvPrefix = "sandbox-1|",
                ApplyTombstones = true,
            }).RunAsync(incr.BackupId);

            // The sibling survives, and the decline is reported rather than silent.
            var sibling = await users.GetEntityAsync<TableEntity>("sandbox-2|alive", "profile");
            Assert.Equal("sibling@example.com", sibling.Value["Email"]);
            Assert.Equal(1, result.TombstonesSkippedOtherEnv);
            Assert.Equal(1, result.TombstonesApplied);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The control: an unscoped restore still applies every delete.
    /// </summary>
    /// <remarks>
    /// Without this, skipping all tombstones would satisfy the assertion above while resurrecting every row
    /// deleted in the window — including GDPR erasures, which is the reason the tombstone pass exists.
    /// </remarks>
    [Fact]
    public async Task An_unscoped_restore_still_applies_every_tombstone()
    {
        var prefix = $"tu{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        var log = Table($"{prefix}Tombstones");

        await users.AddEntityAsync(new TableEntity("erased", "profile") { ["Email"] = "gdpr@example.com" });
        await log.AddEntityAsync(new TableEntity("Users", "erased|profile")
        {
            ["Op"] = "D",
            ["OrigPK"] = "erased",
            ["OrigRK"] = "profile",
            ["DeletedAt"] = DateTimeOffset.UtcNow,
        });

        var dir = Path.Combine(Path.GetTempPath(), $"tu{Guid.NewGuid():N}");
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

            var result = await new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                AllowUnauthenticatedManifest = true,
                TablePrefix = prefix,
                Mode = RestoreMode.Upsert,
                ApplyTombstones = true,
            }).RunAsync(incr.BackupId);

            Assert.Equal(1, result.TombstonesApplied);
            Assert.Equal(0, result.TombstonesSkippedOtherEnv);
            await Assert.ThrowsAsync<Azure.RequestFailedException>(
                () => users.GetEntityAsync<TableEntity>("erased", "profile"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── the rollup signs what it produces ────────────────────────────────────────────────────────────

    /// <summary>
    /// A rolled-up snapshot restores without <c>AllowUnauthenticatedManifest</c>.
    /// </summary>
    /// <remarks>
    /// It could not before: <c>MergeToTargetAsync</c> had no manifest key and never signed, so
    /// <c>RestoreService</c> — which treats a missing MAC as an authentication FAILURE when a key is supplied —
    /// refused the retained copy. Every rollup test in the suite passed
    /// <c>AllowUnauthenticatedManifest = true</c>, which is what made the gap invisible.
    /// </remarks>
    [Fact]
    public async Task A_rolled_up_snapshot_is_signed_and_restores_authenticated()
    {
        var prefix = $"rs{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "one@example.com" });

        var manifestKey = Key(0x5a);
        var dir = Path.Combine(Path.GetTempPath(), $"rs{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var source = new FileSystemBackupSource(dir);

            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
                ManifestKey = manifestKey,
            }).RunAsync();

            var rolled = await new RollupService(source, target).RollupAsync(
                full.BackupId, [], gzip: false, ct: default, newBackupId: null,
                encryptionKey: null, manifestKey: manifestKey);

            Assert.False(string.IsNullOrEmpty(rolled.ManifestMac));
            Assert.True(ManifestAuthentication.Verify(rolled, manifestKey));

            // And the restore accepts it with a key and NO unauthenticated-manifest escape hatch.
            var result = await new RestoreService(_svc, source, new RestoreOptions
            {
                TablePrefix = prefix,
                Mode = RestoreMode.Upsert,
                ManifestKey = manifestKey,
            }).RunAsync(rolled.BackupId);

            Assert.Equal(0, result.TotalErrors);
            Assert.True(result.TotalRestored > 0);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── the rollup verifies what it reads ────────────────────────────────────────────────────────────

    /// <summary>
    /// A tampered input is refused rather than laundered into a freshly hashed, freshly signed snapshot.
    /// </summary>
    /// <remarks>
    /// Archives are plaintext JSONL by default. The attacker edits a data file and leaves the manifest alone;
    /// before this, the next scheduled rollup read the tampered bytes, recorded a hash OVER them, signed the
    /// result, and <c>RollupAndCleanAsync</c> deleted the original — after which the tamper was
    /// indistinguishable from legitimate content, vouched for by the deployment's own key.
    /// </remarks>
    [Fact]
    public async Task A_rollup_refuses_an_input_whose_bytes_do_not_match_its_manifest()
    {
        var prefix = $"rt{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "real@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"rt{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var source = new FileSystemBackupSource(dir);

            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
            }).RunAsync();

            // Tamper with the data file only — exactly what an attacker with write access to the target does.
            var dataFile = Directory.GetFiles(dir, "Users.jsonl", SearchOption.AllDirectories).Single();
            var tampered = (await File.ReadAllTextAsync(dataFile))
                .Replace("real@example.com", "attacker@evil.example");
            await File.WriteAllTextAsync(dataFile, tampered);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RollupService(source, target).RollupAsync(full.BackupId, [], gzip: false));

            Assert.Contains("integrity check failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A file the input manifest lists but the store no longer holds is an error, not an empty read.
    /// </summary>
    /// <remarks>
    /// Otherwise deleting one data file from an input silently dropped every row in it: the merge contributed
    /// nothing for that table and the output manifest recorded the smaller result as correct — removing a whole
    /// table's worth of records in a way that survives every downstream integrity check.
    /// </remarks>
    [Fact]
    public async Task A_rollup_refuses_an_input_with_a_listed_file_missing()
    {
        var prefix = $"rm{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "one@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"rm{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var source = new FileSystemBackupSource(dir);

            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
            }).RunAsync();

            File.Delete(Directory.GetFiles(dir, "Users.jsonl", SearchOption.AllDirectories).Single());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RollupService(source, target).RollupAsync(full.BackupId, [], gzip: false));

            Assert.Contains("does not hold it", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A rollup whose input manifest fails its MAC is refused when a key is supplied.
    /// </summary>
    [Fact]
    public async Task A_rollup_refuses_an_input_whose_manifest_fails_authentication()
    {
        var prefix = $"rf{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "one@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"rf{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var source = new FileSystemBackupSource(dir);

            // Signed with one key; the rollup is handed another.
            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
                ManifestKey = Key(0x11),
            }).RunAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RollupService(source, target).RollupAsync(
                    full.BackupId, [], gzip: false, ct: default, newBackupId: null,
                    encryptionKey: null, manifestKey: Key(0x22)));

            Assert.Contains("failed authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The control: an untampered rollup with no keys still works, so existing callers are unaffected.
    /// </summary>
    /// <remarks>
    /// Hash verification is unconditional now — it needs no key, only the input's own manifest — so this is the
    /// assertion that it verifies rather than merely refuses.
    /// </remarks>
    [Fact]
    public async Task An_untampered_rollup_with_no_keys_still_succeeds()
    {
        var prefix = $"ro{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "one@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"ro{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var source = new FileSystemBackupSource(dir);

            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
            }).RunAsync();

            var rolled = await new RollupService(source, target).RollupAsync(full.BackupId, [], gzip: false);

            Assert.True(rolled.TotalEntities > 0);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── the table set is complete ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every data table the Azure provider creates is either backed up or excluded by name, with a reason.
    /// </summary>
    /// <remarks>
    /// <c>BackupDefaults.Tables</c> documents itself as "all Authagonal data tables" and omitted three:
    /// <c>AgentProfiles</c>, <c>UserRoles</c> and <c>UpstreamRefreshTokens</c> — the last of which was already
    /// named in <c>SecretBearingTables</c> as though it were in the archive. None of the absences was
    /// fail-safe. A restored deployment came up with the agent ceiling, consent requirement, approval gate and
    /// delegation budget all absent, and every gate that enforces them lives inside
    /// <c>if (agentProfile is not null)</c>.
    /// <para>
    /// A convention test rather than three added strings, because the next table added is the one nobody will
    /// think about.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_provider_table_is_backed_up_or_excluded_with_a_reason()
    {
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tombstones"] = "The change log itself. Backed up as the _tombstones file by the engine, not as a "
                + "data table.",
            ["SamlReplayCache"] = "Transient: entries expire on the assertion validity window.",
            ["OidcStateStore"] = "Transient: entries live for one authorization round trip.",
            ["RevokedTokens"] = "Transient: bounded by access-token lifetime, typically minutes.",
            ["RateLimitCounters"] = "Transient counters. Restoring them would reinstate stale budgets.",
            ["TokenCredential"] = "Data-protection key ring, managed by its own key-management path.",
            ["MigrationState"] = "Run-once markers for this deployment. Restoring them re-arms or suppresses "
                + "migrations for the wrong deployment.",
        };

        var providerSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Authagonal.AzureProvider", "ServiceCollectionExtensions.cs"));

        // The table names the provider actually creates, as they appear in its own registration.
        var created = System.Text.RegularExpressions.Regex
            .Matches(providerSource, @"""(?<name>[A-Z][A-Za-z]{3,})""")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(n => n is not ("Authagonal" or "Azure" or "Storage" or "Table" or "Tables"))
            .ToList();

        Assert.NotEmpty(created);

        var missing = created
            .Where(n => !BackupDefaults.Tables.Contains(n, StringComparer.Ordinal))
            .Where(n => !excluded.ContainsKey(n))
            .ToList();

        Assert.True(missing.Count == 0,
            "These tables are created by the Azure provider but are neither in BackupDefaults.Tables nor "
            + "excluded with a reason. An omission here is not fail-safe: a restore brings the deployment up "
            + "with that table EMPTY, and code that treats an absent row as 'no policy configured' then runs "
            + "with the policy off. Add the table, or exclude it by name with the reason it is transient. "
            + "Missing: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Every table named as secret-bearing is actually in the archive.
    /// </summary>
    /// <remarks>
    /// The inventory the CLI prints before it writes has to describe what it is about to write.
    /// <c>UpstreamRefreshTokens</c> was listed there — "live refresh tokens for upstream identity providers" —
    /// while the backup never included the table, so the warning was simultaneously alarming and false, and it
    /// concealed the omission by making the table look handled.
    /// </remarks>
    [Fact]
    public void Every_secret_bearing_table_is_in_the_backup_set()
    {
        var absent = BackupDefaults.SecretBearingTables.Keys
            .Where(t => !BackupDefaults.Tables.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(absent.Count == 0,
            "These tables are described to the operator as secret-bearing contents of the archive, but the "
            + "backup does not write them: " + string.Join(", ", absent));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
