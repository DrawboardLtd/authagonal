using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// The organization membership index: "who is in this organization", answered from a single partition
/// instead of by paging every user and filtering in memory.
/// </summary>
/// <remarks>
/// <c>OrganizationId</c> is a column on the user profile, so the reverse question had no index and the
/// list endpoint scanned. That is slow, and it is also wrong past the scan's page cap: a sparse
/// organization came back short with nothing to say the answer was truncated. These run against
/// Azurite, so the partition queries, projections and continuation tokens are the real ones.
/// </remarks>
[Collection("Azurite")]
public class OrganizationIndexTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private (TableUserStore Store, TableClient Index) NewStore(string prefix, bool withIndex = true)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        var index = T("UserOrgs");
        var store = new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null,
            EnvPartitioner.Live, userOrganizationsTable: withIndex ? index : null);
        return (store, index);
    }

    private static AuthUser User(string id, string email, string? org) => new()
    {
        Id = id,
        Email = email.ToLowerInvariant(),
        NormalizedEmail = email.ToUpperInvariant(),
        IsActive = true,
        OrganizationId = org,
    };

    private static string P() => $"oi{Guid.NewGuid():N}"[..12];

    // ── The read path ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListsTheMembersOfAnOrganization()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.CreateAsync(User("u2", "grace@acme.test", "org-acme"));
        await store.CreateAsync(User("u3", "linus@other.test", "org-other"));
        await store.MarkOrganizationIndexCompleteAsync();

        var page = await store.ListPageAsync("org-acme", 50, null);

        Assert.Equal(["u1", "u2"], page.Users.Select(u => u.Id).OrderBy(id => id));
    }

    /// <summary>
    /// The point of the whole change: an organization's page must not cost a walk of the tenant. The
    /// scan gave up after a bounded number of pages, so a member sitting behind enough non-members was
    /// simply absent from the answer. Here every non-member is a user the index must not read.
    /// </summary>
    [Fact]
    public async Task FindsAMemberBuriedBehindManyNonMembers()
    {
        var (store, _) = NewStore(P());
        for (var i = 0; i < 60; i++)
            await store.CreateAsync(User($"n{i:D3}", $"n{i:D3}@other.test", "org-other"));
        await store.CreateAsync(User("zzz", "needle@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        var page = await store.ListPageAsync("org-acme", 10, null);

        Assert.Equal(["zzz"], page.Users.Select(u => u.Id));
    }

    [Fact]
    public async Task PagesThroughAnOrganizationWithAContinuationToken()
    {
        var (store, _) = NewStore(P());
        for (var i = 0; i < 12; i++)
            await store.CreateAsync(User($"u{i:D2}", $"u{i:D2}@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await store.ListPageAsync("org-acme", 5, token);
            seen.AddRange(page.Users.Select(u => u.Id));
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(12, seen.Count);
        Assert.Equal(12, seen.Distinct().Count());
    }

    [Fact]
    public async Task OffsetPagingSkipsOnTheIndex()
    {
        var (store, _) = NewStore(P());
        for (var i = 0; i < 6; i++)
            await store.CreateAsync(User($"u{i}", $"u{i}@acme.test", "org-acme"));
        await store.CreateAsync(User("x", "x@other.test", "org-other"));
        await store.MarkOrganizationIndexCompleteAsync();

        var (users, hasMore) = await store.ListAsync("org-acme", startIndex: 2, count: 2);

        Assert.Equal(2, users.Count);
        Assert.True(hasMore);
        Assert.DoesNotContain("x", users.Select(u => u.Id));
    }

    [Fact]
    public async Task AnUnfilteredListIsUnaffected()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.CreateAsync(User("u2", "nobody@acme.test", null));
        await store.MarkOrganizationIndexCompleteAsync();

        var page = await store.ListPageAsync(null, 50, null);

        Assert.Equal(["u1", "u2"], page.Users.Select(u => u.Id).OrderBy(id => id));
    }

    // ── Index maintenance ────────────────────────────────────────────────────

    [Fact]
    public async Task MovingAUserBetweenOrganizationsMovesTheirRow()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        var user = await store.GetAsync("u1");
        user!.OrganizationId = "org-other";
        await store.UpdateAsync(user);

        Assert.Empty((await store.ListPageAsync("org-acme", 50, null)).Users);
        Assert.Equal(["u1"], (await store.ListPageAsync("org-other", 50, null)).Users.Select(u => u.Id));
    }

    [Fact]
    public async Task ClearingAnOrganizationRemovesTheRow()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        var user = await store.GetAsync("u1");
        user!.OrganizationId = null;
        await store.UpdateAsync(user);

        Assert.Empty((await store.ListPageAsync("org-acme", 50, null)).Users);
    }

    [Fact]
    public async Task AssigningAnOrganizationToAUserWhoHadNoneAddsTheRow()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", null));
        await store.MarkOrganizationIndexCompleteAsync();

        var user = await store.GetAsync("u1");
        user!.OrganizationId = "org-acme";
        await store.UpdateAsync(user);

        Assert.Equal(["u1"], (await store.ListPageAsync("org-acme", 50, null)).Users.Select(u => u.Id));
    }

    /// <summary>
    /// A deleted account that keeps answering "who is in this organization" is the same defect as a
    /// stale role membership, and worse in context: the listing is what an operator reads to decide who
    /// still has access.
    /// </summary>
    [Fact]
    public async Task DeletingAUserRemovesTheirMembership()
    {
        var (store, index) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.CreateAsync(User("u2", "grace@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        await store.DeleteAsync("u1");

        Assert.Equal(["u2"], (await store.ListPageAsync("org-acme", 50, null)).Users.Select(u => u.Id));

        // Not merely filtered out on read — the row is gone.
        var pk = UserOrganizationEntity.KeyFor("org-acme");
        var rows = new List<UserOrganizationEntity>();
        await foreach (var r in index.QueryAsync<UserOrganizationEntity>(e => e.PartitionKey == pk))
            rows.Add(r);
        Assert.Equal(["u2"], rows.Select(r => r.UserId));
    }

    /// <summary>
    /// An index row whose user has gone is skipped, not fatal. The index is a convenience over the user
    /// store, never the authority on who exists.
    /// </summary>
    [Fact]
    public async Task AStrandedIndexRowIsSkipped()
    {
        var (store, index) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.MarkOrganizationIndexCompleteAsync();

        var orphan = UserOrganizationEntity.Create("org-acme", "ghost");
        await index.UpsertEntityAsync(orphan, TableUpdateMode.Replace);

        var page = await store.ListPageAsync("org-acme", 50, null);

        Assert.Equal(["u1"], page.Users.Select(u => u.Id));
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The id comes from a customer's provisioning app as free text, and Azure Table forbids '/', '\',
    /// '#' and '?' in a key. Because the index is written inside user creation, a raw key would turn a
    /// downstream app's choice of id format into "this user cannot be created".
    /// </summary>
    [Theory]
    [InlineData("urn:acme/eu/finance")]
    [InlineData("acme#1")]
    [InlineData("who?/what\\where")]
    [InlineData("orgs/2026/new")]
    public async Task AnIdContainingCharactersIllegalInAKeyStillWorks(string organizationId)
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", organizationId));
        await store.MarkOrganizationIndexCompleteAsync();

        var page = await store.ListPageAsync(organizationId, 50, null);

        Assert.Equal(["u1"], page.Users.Select(u => u.Id));
    }

    /// <summary>
    /// The scan this replaces compared ordinally. Folding case here would silently merge two ids a
    /// customer had deliberately kept apart, which is a data change wearing a performance fix's clothes.
    /// </summary>
    [Fact]
    public async Task OrganizationIdsAreMatchedOrdinally()
    {
        var (store, _) = NewStore(P());
        await store.CreateAsync(User("u1", "ada@acme.test", "acme"));
        await store.CreateAsync(User("u2", "grace@acme.test", "ACME"));
        await store.MarkOrganizationIndexCompleteAsync();

        Assert.Equal(["u1"], (await store.ListPageAsync("acme", 50, null)).Users.Select(u => u.Id));
        Assert.Equal(["u2"], (await store.ListPageAsync("ACME", 50, null)).Users.Select(u => u.Id));
    }

    [Fact]
    public void TheCoverageMarkerCannotCollideWithAnOrganizationKey()
    {
        // KeyFor is always 64 hex characters, so a literal marker key is unreachable from any id.
        Assert.NotEqual(UserOrganizationEntity.CoverageMarkerKey, UserOrganizationEntity.KeyFor("coverage"));
        Assert.Equal(64, UserOrganizationEntity.KeyFor("coverage").Length);
    }

    // ── The coverage gate ────────────────────────────────────────────────────

    /// <summary>
    /// The reason the gate exists. Users created before the index have no rows, so trusting the index
    /// straight away would answer "nobody is in this organization" for every one of them — a silently
    /// truncated list, which is the defect the index was built to remove.
    /// </summary>
    [Fact]
    public async Task BeforeTheBackfillTheScanStillAnswersCorrectly()
    {
        var prefix = P();
        var (bare, _) = NewStore(prefix, withIndex: false);
        await bare.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await bare.CreateAsync(User("u2", "linus@other.test", "org-other"));

        // Same tables, now with the index configured but never backfilled.
        var (store, _) = NewStore(prefix);

        var page = await store.ListPageAsync("org-acme", 50, null);

        Assert.Equal(["u1"], page.Users.Select(u => u.Id));
    }

    [Fact]
    public async Task TheBackfillIndexesUsersThatPredateTheIndex()
    {
        var prefix = P();
        var (bare, _) = NewStore(prefix, withIndex: false);
        for (var i = 0; i < 5; i++)
            await bare.CreateAsync(User($"u{i}", $"u{i}@acme.test", "org-acme"));
        await bare.CreateAsync(User("x", "x@none.test", null));

        var (store, index) = NewStore(prefix);
        var written = await store.MigrateOrganizationIndexAsync(dryRun: false);

        Assert.Equal(5, written);

        var pk = UserOrganizationEntity.KeyFor("org-acme");
        var rows = new List<UserOrganizationEntity>();
        await foreach (var r in index.QueryAsync<UserOrganizationEntity>(e => e.PartitionKey == pk))
            rows.Add(r);
        Assert.Equal(5, rows.Count);

        // And the listing now comes off the index.
        Assert.Equal(5, (await store.ListPageAsync("org-acme", 50, null)).Users.Count);
    }

    [Fact]
    public async Task ADryRunCountsWithoutWriting()
    {
        var prefix = P();
        var (bare, _) = NewStore(prefix, withIndex: false);
        await bare.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await bare.CreateAsync(User("u2", "x@none.test", null));

        var (store, index) = NewStore(prefix);
        var would = await store.MigrateOrganizationIndexAsync(dryRun: true);

        Assert.Equal(1, would);

        var any = false;
        await foreach (var _ in index.QueryAsync<TableEntity>()) { any = true; break; }
        Assert.False(any, "a dry run must not write the index rows or the coverage marker");
    }

    [Fact]
    public async Task TheBackfillIsIdempotent()
    {
        var prefix = P();
        var (bare, _) = NewStore(prefix, withIndex: false);
        await bare.CreateAsync(User("u1", "ada@acme.test", "org-acme"));

        var (store, _) = NewStore(prefix);
        await store.MigrateOrganizationIndexAsync(dryRun: false);
        await store.MigrateOrganizationIndexAsync(dryRun: false);

        Assert.Equal(["u1"], (await store.ListPageAsync("org-acme", 50, null)).Users.Select(u => u.Id));
    }

    /// <summary>
    /// A tenant whose users all postdate the index has a complete index by construction, and would
    /// otherwise keep taking the scan path until someone ran a backfill it never needed.
    /// </summary>
    [Fact]
    public async Task AFreshStoreCanBeMarkedCompleteWithoutAScan()
    {
        var (store, _) = NewStore(P());
        await store.MarkOrganizationIndexCompleteAsync();
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));

        Assert.Equal(["u1"], (await store.ListPageAsync("org-acme", 50, null)).Users.Select(u => u.Id));
    }

    /// <summary>
    /// A store with no index table configured must keep filtering, not start answering empty.
    /// </summary>
    [Fact]
    public async Task WithNoIndexTableTheFilterStillWorks()
    {
        var (store, _) = NewStore(P(), withIndex: false);
        await store.CreateAsync(User("u1", "ada@acme.test", "org-acme"));
        await store.CreateAsync(User("u2", "linus@other.test", "org-other"));

        Assert.Equal(["u1"], (await store.ListPageAsync("org-acme", 50, null)).Users.Select(u => u.Id));
        Assert.Equal(0, await store.MigrateOrganizationIndexAsync(dryRun: false));
    }
}
