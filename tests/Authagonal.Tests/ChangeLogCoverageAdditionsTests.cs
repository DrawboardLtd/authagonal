using System.Security.Cryptography;
using System.Text;
using Authagonal.Backup;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Change-log capture for the tables added to the backup set (F16): ScimGroupRoleMappings and
/// ProvisioningApps log upserts + deletes, and the two blind-index tables (UserEmailDomains,
/// UserEmailLocalPrefixes) log their upserts, so all four qualify for
/// <see cref="BackupDefaults.ChangeLoggedTables"/>. Also the F17 rollup-id fix: a rollup can be
/// minted under an explicit backup id (how the weekly snapshot gets its physical "-weekly" id).
/// Azurite.
/// </summary>
[Collection("Azurite")]
public class ChangeLogCoverageAdditionsTests(AzuriteFixture azurite)
{
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

    private async Task<List<TableEntity>> Rows(TableClient log, string changeTable, string? op = null)
    {
        var rows = new List<TableEntity>();
        await foreach (var e in log.QueryAsync<TableEntity>(e => e.PartitionKey == changeTable))
            if (op is null || e.GetString("Op") == op) rows.Add(e);
        return rows;
    }

    [Fact]
    public async Task ScimGroupRoleMappingStore_logs_upsert_and_delete()
    {
        var prefix = $"sm{Guid.NewGuid():N}";
        var log = Table(prefix, "Tombstones");
        var store = new TableScimGroupRoleMappingStore(
            Table(prefix, "ScimGroupRoleMappings"), EnvPartitioner.Live, new TableChangeWriter(log));

        await store.SetAsync(new ScimGroupRoleMapping { GroupId = "g1", Role = "tenant:admin" });
        var upserts = await Rows(log, "ScimGroupRoleMappings", "U");
        var row = Assert.Single(upserts);
        Assert.NotNull(row.GetString("OrigPK"));
        Assert.NotNull(row.GetString("OrigRK"));

        await store.DeleteAsync("g1", "tenant:admin");
        Assert.Empty(await Rows(log, "ScimGroupRoleMappings", "U")); // last-op-wins collapsed the key
        Assert.Single(await Rows(log, "ScimGroupRoleMappings", "D"));
    }

    [Fact]
    public async Task ProvisioningAppStore_logs_upsert()
    {
        var prefix = $"pa{Guid.NewGuid():N}";
        var log = Table(prefix, "Tombstones");
        var store = new TableProvisioningAppStore(
            Table(prefix, "ProvisioningApps"), EnvPartitioner.Live, new TableChangeWriter(log));

        await store.UpsertAsync(new ProvisioningAppConfig { AppId = "app1", Name = "App", CallbackUrl = "https://x.test" });
        Assert.Single(await Rows(log, "ProvisioningApps", "U"));

        await store.DeleteAsync("app1", default);
        Assert.Single(await Rows(log, "ProvisioningApps", "D"));
    }

    [Fact]
    public async Task UserStore_logs_email_domain_and_local_prefix_index_upserts()
    {
        var prefix = $"dx{Guid.NewGuid():N}";
        var log = Table(prefix, "Tombstones");
        var store = new TableUserStore(
            Table(prefix, "Users"), Table(prefix, "UserEmails"), Table(prefix, "UserLogins"),
            Table(prefix, "UserExternalIds"), Table(prefix, "UserFirstNames"), Table(prefix, "UserLastNames"),
            EnvPartitioner.Live, tombstoneWriter: new TableChangeWriter(log),
            indexTokenizer: new FakeTokenizer(),
            userEmailDomainsTable: Table(prefix, "UserEmailDomains"),
            userEmailLocalPrefixesTable: Table(prefix, "UserEmailLocalPrefixes"));

        await store.CreateAsync(new AuthUser
        {
            Id = "u1",
            Email = "ada@acme.test",
            NormalizedEmail = "ADA@ACME.TEST",
        });

        Assert.Single(await Rows(log, "UserEmailDomains", "U"));
        Assert.NotEmpty(await Rows(log, "UserEmailLocalPrefixes", "U")); // one row per local-part prefix
    }

    [Fact]
    public async Task Rollup_honors_explicit_backup_id()
    {
        var prefix = $"ri{Guid.NewGuid():N}";
        var users = Table(prefix, "Users");
        await users.AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@b.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"ri{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target,
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            var source = new FileSystemBackupSource(dir);
            var weeklyId = $"{full.BackupId}-weekly";
            var weekly = await new RollupService(source, target)
                .RollupAsync(full.BackupId, [], gzip: false, newBackupId: weeklyId);

            Assert.Equal(weeklyId, weekly.BackupId);
            // The physical id carries the suffix — the manifest is readable under it and the id
            // shows up in the backup listing, so id-based retention/selection can see it.
            var reread = await source.ReadManifestAsync(weeklyId);
            Assert.NotNull(reread);
            Assert.Contains(weeklyId, await source.ListBackupIdsAsync());
            // Data survived the merge into the renamed snapshot.
            await using var stream = await source.OpenReadAsync(weeklyId, "Users.jsonl");
            Assert.NotNull(stream);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
