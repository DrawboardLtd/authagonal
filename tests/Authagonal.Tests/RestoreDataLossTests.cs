using Authagonal.Backup;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Three ways <see cref="RestoreMode.Clean"/> destroyed data that the operator never asked it to touch:
/// a dry run that deleted for real, a clean restore of an incremental leaving only the delta, and an
/// unscoped wipe taking out sibling envs sharing the physical table. None involve an attacker — the tool
/// did this on its own, and <c>--dry-run</c> was the most dangerous flag it offered.
/// </summary>
[Collection("Azurite")]
public class RestoreDataLossTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient Table(string name)
    {
        var c = _svc.GetTableClient(name);
        c.CreateIfNotExists();
        return c;
    }

    private static async Task<int> CountAsync(TableClient t)
    {
        var n = 0;
        await foreach (var _ in t.QueryAsync<TableEntity>(select: new[] { "RowKey" })) n++;
        return n;
    }

    /// <summary>
    /// `--dry-run --mode clean`: CleanTableAsync ran before the DryRun check that guarded the writes, so
    /// the "show me what would happen" flag emptied the table and restored nothing.
    /// </summary>
    [Fact]
    public async Task DryRun_clean_deletes_nothing()
    {
        var prefix = $"dr{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@example.com" });
        await users.AddEntityAsync(new TableEntity("u2", "profile") { ["Email"] = "b@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"dr{Guid.NewGuid():N}");
        try
        {
            var full = await new BackupService(_svc, new FileSystemBackupTarget(dir),
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            // Add a row that is NOT in the backup, so a real clean would be observable.
            await users.AddEntityAsync(new TableEntity("u3", "profile") { ["Email"] = "c@example.com" });
            Assert.Equal(3, await CountAsync(users));

            await new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                TablePrefix = prefix,
                Mode = RestoreMode.Clean,
                DryRun = true,
            }).RunAsync(full.BackupId);

            // Nothing deleted, nothing written.
            Assert.Equal(3, await CountAsync(users));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A clean restore of an incremental empties the table and then writes back only the changed rows,
    /// destroying every row that did not change in the window. The manifest recorded the mode all along.
    /// </summary>
    [Fact]
    public async Task Clean_restore_of_an_incremental_is_refused()
    {
        var prefix = $"ci{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"ci{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target,
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            // One changed row after the full → an incremental holding just that row.
            await users.AddEntityAsync(new TableEntity("u2", "profile") { ["Email"] = "b@example.com" });
            var incr = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
                Incremental = true,
                WatermarkOverride = DateTimeOffset.UtcNow.AddMinutes(-1),
            }).RunAsync();

            var before = await CountAsync(users);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
                {
                    TablePrefix = prefix,
                    Mode = RestoreMode.Clean,
                }).RunAsync(incr.BackupId));

            Assert.Contains("incremental", ex.Message, StringComparison.OrdinalIgnoreCase);
            // Refused before touching anything.
            Assert.Equal(before, await CountAsync(users));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Sandbox envs share one physical table, keyed <c>{env}|{natural}</c>. An unscoped clean restore of
    /// one env's backup wiped every sibling env in the table.
    /// </summary>
    [Fact]
    public async Task Clean_scoped_to_an_env_leaves_sibling_envs_intact()
    {
        var prefix = $"ev{Guid.NewGuid():N}";
        var users = Table($"{prefix}Users");

        await users.AddEntityAsync(new TableEntity("sandbox-1|u1", "profile") { ["Email"] = "one@example.com" });
        await users.AddEntityAsync(new TableEntity("sandbox-2|u1", "profile") { ["Email"] = "two@example.com" });
        // A key with a non-ASCII character: a '~' upper bound would skip it and leave it behind.
        await users.AddEntityAsync(new TableEntity("sandbox-1|üser", "profile") { ["Email"] = "u@example.com" });

        var dir = Path.Combine(Path.GetTempPath(), $"ev{Guid.NewGuid():N}");
        try
        {
            var full = await new BackupService(_svc, new FileSystemBackupTarget(dir),
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            await new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                TablePrefix = prefix,
                Mode = RestoreMode.Clean,
                CleanEnvPrefix = "sandbox-1|",
            }).RunAsync(full.BackupId);

            // sandbox-2 survived the wipe (it is restored from the backup regardless, so assert it exists).
            var sibling = await users.GetEntityAsync<TableEntity>("sandbox-2|u1", "profile");
            Assert.Equal("two@example.com", sibling.Value["Email"]);

            // The non-ASCII key inside the cleaned env was reachable by the range filter and restored.
            var nonAscii = await users.GetEntityAsync<TableEntity>("sandbox-1|üser", "profile");
            Assert.Equal("u@example.com", nonAscii.Value["Email"]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
