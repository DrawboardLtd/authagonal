using Authagonal.Core.Services;
using Authagonal.Core.Models;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// The role membership index: "who holds this role", answered from a single partition instead of by
/// reading every user.
/// </summary>
/// <remarks>
/// Roles live as a list on the user, which answers the opposite question. Without a reverse index an
/// admin console has to page all accounts and filter, which is the wrong shape at any size worth
/// having — and the reason "who administers this" was previously not answerable at all. Azurite.
/// </remarks>
[Collection("Azurite")]
public class RoleMembershipIndexTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, bool withRoleIndex = true)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null,
            EnvPartitioner.Live, userRolesTable: withRoleIndex ? T("UserRoles") : null);
    }

    private static AuthUser User(string id, string email, params string[] roles) => new()
    {
        Id = id,
        Email = email.ToLowerInvariant(),
        NormalizedEmail = email.ToUpperInvariant(),
        IsActive = true,
        Roles = [.. roles],
    };

    [Fact]
    public async Task ListsTheUsersHoldingARole()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12]);
        await store.CreateAsync(User("u1", "ada@example.com", "staff-admin"));
        await store.CreateAsync(User("u2", "grace@example.com", "staff-admin", "billing"));
        await store.CreateAsync(User("u3", "linus@example.com", "billing"));

        var admins = await store.ListUsersInRoleAsync("staff-admin");

        Assert.Equal(["u1", "u2"], admins.Select(u => u.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task IsCaseInsensitiveOnTheRoleName()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12]);
        await store.CreateAsync(User("u1", "ada@example.com", "Staff-Admin"));

        Assert.Single(await store.ListUsersInRoleAsync("staff-ADMIN"));
    }

    [Fact]
    public async Task GrantingARole_AddsTheMember()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12]);
        await store.CreateAsync(User("u1", "ada@example.com"));

        Assert.Empty(await store.ListUsersInRoleAsync("staff-admin"));

        var user = (await store.GetAsync("u1"))!;
        user.Roles.Add("staff-admin");
        await store.UpdateAsync(user);

        Assert.Single(await store.ListUsersInRoleAsync("staff-admin"));
    }

    [Fact]
    public async Task RevokingARole_RemovesTheMember_AndLeavesTheOthersAlone()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12]);
        await store.CreateAsync(User("u1", "ada@example.com", "staff-admin", "billing"));

        var user = (await store.GetAsync("u1"))!;
        user.Roles.Remove("staff-admin");
        await store.UpdateAsync(user);

        Assert.Empty(await store.ListUsersInRoleAsync("staff-admin"));
        // The role that did not change must not have been rewritten out from under the user.
        Assert.Single(await store.ListUsersInRoleAsync("billing"));
    }

    /// <summary>
    /// A deleted account must stop answering "who administers this" — otherwise the index outlives
    /// the person and a console shows an administrator nobody can reach.
    /// </summary>
    [Fact]
    public async Task DeletingAUser_RemovesTheirMemberships()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12]);
        await store.CreateAsync(User("u1", "ada@example.com", "staff-admin"));
        await store.CreateAsync(User("u2", "grace@example.com", "staff-admin"));

        await store.DeleteAsync("u1");

        var admins = await store.ListUsersInRoleAsync("staff-admin");
        Assert.Equal(["u2"], admins.Select(u => u.Id));
    }

    /// <summary>
    /// The backfill case, and the one that matters most in practice: an account that existed before
    /// the index did is invisible to it until reindexed. Without this, the index describes only
    /// accounts touched since it shipped — and a role granted years ago answers nothing.
    /// </summary>
    [Fact]
    public async Task Reindex_BackfillsAUserWrittenBeforeTheIndexExisted()
    {
        var prefix = $"rmi{Guid.NewGuid():N}"[..12];
        var withoutIndex = NewStore(prefix, withRoleIndex: false);
        await withoutIndex.CreateAsync(User("u1", "ada@example.com", "staff-admin"));

        var withIndex = NewStore(prefix);
        Assert.Empty(await withIndex.ListUsersInRoleAsync("staff-admin"));

        await withIndex.ReindexUserAsync("u1");

        Assert.Single(await withIndex.ListUsersInRoleAsync("staff-admin"));
    }

    /// <summary>
    /// A membership row whose user has since vanished is skipped, not thrown on — the index is a
    /// convenience over the user store, never the authority on who exists.
    /// </summary>
    [Fact]
    public async Task AStrandedRow_IsSkippedRatherThanFailingTheQuery()
    {
        var prefix = $"rmi{Guid.NewGuid():N}"[..12];
        var store = NewStore(prefix);
        await store.CreateAsync(User("u1", "ada@example.com", "staff-admin"));
        await store.CreateAsync(User("u2", "grace@example.com", "staff-admin"));

        // Delete the profile behind u1's back, leaving its index row orphaned.
        var users = _svc.GetTableClient($"{prefix}Users");
        await users.DeleteEntityAsync("u1", "profile");

        var admins = await store.ListUsersInRoleAsync("staff-admin");
        Assert.Equal(["u2"], admins.Select(u => u.Id));
    }

    /// <summary>
    /// An empty answer to "who holds this role" is indistinguishable from "nobody does", and code
    /// acting on that distinction is deciding who administers something. A store with no index says
    /// so instead.
    /// </summary>
    [Fact]
    public async Task WithoutTheIndex_ItThrowsRatherThanAnsweringEmpty()
    {
        var store = NewStore($"rmi{Guid.NewGuid():N}"[..12], withRoleIndex: false);
        await store.CreateAsync(User("u1", "ada@example.com", "staff-admin"));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.ListUsersInRoleAsync("staff-admin"));
    }
}
