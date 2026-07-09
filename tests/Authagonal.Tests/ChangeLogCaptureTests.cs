using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Change-log capture (Increment 1b): TableUserStore records an Op="U" change-log row on every write to the
/// six backed-up user tables, the upsert-side mirror of the existing tombstone (delete) capture. One row per
/// key (upsert-replace), so a create-then-delete of the same key collapses to a single Op="D" row. Login-
/// state-only writes are deliberately not logged. Verified against real Azure Table semantics (Azurite).
/// </summary>
[Collection("Azurite")]
public class ChangeLogCaptureTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    // Physically the "Tombstones" table (now the unified change-log); pass a TableChangeWriter as the store's
    // change-writer and it stamps Op="U"/"D" on each row (PK = logical table, RK = "{pk}|{rk}").
    private (TableUserStore store, TableClient log) NewStore(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        var log = T("Tombstones");
        var store = new TableUserStore(
            T("Users"), T("UserEmails"), T("UserLogins"), T("UserExternalIds"), T("UserFirstNames"), T("UserLastNames"),
            EnvPartitioner.Live, tombstoneWriter: new TableChangeWriter(log),
            userEmailDomainsTable: T("UserEmailDomains"), userEmailLocalPrefixesTable: T("UserEmailLocalPrefixes"));
        return (store, log);
    }

    private async Task<List<TableEntity>> Rows(TableClient log, string changeTable, string? op = null)
    {
        var rows = new List<TableEntity>();
        await foreach (var e in log.QueryAsync<TableEntity>(e => e.PartitionKey == changeTable))
            if (op is null || e.GetString("Op") == op) rows.Add(e);
        return rows;
    }

    private static AuthUser User(string id, string email, string first, string last) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
    };

    [Fact]
    public async Task Create_logs_upsert_rows_for_profile_and_indexes()
    {
        var (store, log) = NewStore($"cl{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));

        Assert.Single(await Rows(log, "Users", "U"));         // one profile row
        Assert.NotEmpty(await Rows(log, "UserEmails", "U"));  // email lookup index
        Assert.NotEmpty(await Rows(log, "UserFirstNames", "U"));
        Assert.NotEmpty(await Rows(log, "UserLastNames", "U"));
        Assert.Empty(await Rows(log, "Users", "D"));          // nothing deleted on a fresh create
    }

    [Fact]
    public async Task Delete_collapses_the_profile_row_to_a_single_delete()
    {
        var (store, log) = NewStore($"cl{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.DeleteAsync("u1");

        // Same key was upserted (U) then deleted (D); last-op-wins leaves one D row and no U row.
        Assert.Empty(await Rows(log, "Users", "U"));
        Assert.Single(await Rows(log, "Users", "D"));
        Assert.NotEmpty(await Rows(log, "UserEmails", "D"));
    }

    [Fact]
    public async Task Update_email_logs_the_new_email_index_and_profile_upserts()
    {
        var (store, log) = NewStore($"cl{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        await store.UpdateAsync(User("u1", "grace@acme.test", "Ada", "Lovelace"));

        Assert.NotEmpty(await Rows(log, "UserEmails", "U")); // new email key logged
        Assert.NotEmpty(await Rows(log, "UserEmails", "D")); // old email key tombstoned
        Assert.Single(await Rows(log, "Users", "U"));        // profile re-upserted, still one row
    }

    [Fact]
    public async Task RecordSuccessfulLogin_does_not_log_an_upsert()
    {
        var (store, log) = NewStore($"cl{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "ada@acme.test", "Ada", "Lovelace"));
        var before = (await Rows(log, "Users")).Count;

        await store.RecordSuccessfulLoginAsync("u1");

        // Login-state-only write is deliberately not change-logged (hot path, low-value fields).
        Assert.Equal(before, (await Rows(log, "Users")).Count);
    }
}
