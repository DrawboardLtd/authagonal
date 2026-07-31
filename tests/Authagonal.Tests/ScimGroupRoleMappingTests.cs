using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// SCIM group → role mapping resolution at token issuance. A user's effective roles are
/// their directly-assigned roles unioned with the roles granted by every group they belong
/// to. An empty mapping store must be a no-op (effective roles == directly-assigned roles).
/// </summary>
public sealed class ScimGroupRoleMappingTests
{
    private static UserStoreOidcSubjectResolver NewResolver(
        InMemoryScimGroupStore groups,
        WritableScimGroupRoleMappingStore mappings) =>
        ResolverTestSupport.NewResolver(new InMemoryUserStore(), groups, mappings, new InMemoryClientStore());

    private static AuthUser User(string id, params string[] roles) => new()
    {
        Id = id,
        Email = $"{id}@acme.test",
        NormalizedEmail = $"{id}@acme.test".ToUpperInvariant(),
        Roles = [.. roles],
    };

    private static ScimGroup Group(string id, string displayName, params string[] memberUserIds) => new()
    {
        Id = id,
        DisplayName = displayName,
        MemberUserIds = [.. memberUserIds],
    };

    [Fact]
    public async Task EmptyMappingStore_LeavesRolesUnchanged()
    {
        var resolver = NewResolver(new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore());

        var subject = await resolver.BuildSubjectAsync(User("u1", "developer"), client: null);

        Assert.Equal(["developer"], subject.Roles);
    }

    [Fact]
    public async Task GroupMembership_GrantsMappedRole()
    {
        var groups = new InMemoryScimGroupStore();
        await groups.CreateAsync(Group("g-admins", "Admins", "u1"));
        var mappings = new WritableScimGroupRoleMappingStore();
        await mappings.SetAsync(new ScimGroupRoleMapping { GroupId = "g-admins", Role = "admin" });

        var subject = await NewResolver(groups, mappings).BuildSubjectAsync(User("u1"), client: null);

        Assert.Equal(["admin"], subject.Roles);
    }

    [Fact]
    public async Task DirectAndGroupRoles_AreUnioned_WithoutDuplicates()
    {
        var groups = new InMemoryScimGroupStore();
        await groups.CreateAsync(Group("g-admins", "Admins", "u1"));
        await groups.CreateAsync(Group("g-sre", "SRE", "u1"));
        var mappings = new WritableScimGroupRoleMappingStore();
        await mappings.SetAsync(new ScimGroupRoleMapping { GroupId = "g-admins", Role = "admin" });
        await mappings.SetAsync(new ScimGroupRoleMapping { GroupId = "g-sre", Role = "sre" });

        // u1 is directly a "developer" and also gets "admin" (dup with nothing) + "sre" via groups.
        var subject = await NewResolver(groups, mappings).BuildSubjectAsync(User("u1", "developer", "admin"), client: null);

        Assert.NotNull(subject.Roles);
        Assert.Equal(["admin", "developer", "sre"], subject.Roles!.OrderBy(r => r));
    }

    [Fact]
    public async Task MappingForGroupUserIsNotIn_DoesNotGrantRole()
    {
        var groups = new InMemoryScimGroupStore();
        await groups.CreateAsync(Group("g-admins", "Admins", "someone-else"));
        var mappings = new WritableScimGroupRoleMappingStore();
        await mappings.SetAsync(new ScimGroupRoleMapping { GroupId = "g-admins", Role = "admin" });

        var subject = await NewResolver(groups, mappings).BuildSubjectAsync(User("u1", "developer"), client: null);

        Assert.Equal(["developer"], subject.Roles);
    }

    [Fact]
    public async Task NoRolesAtAll_YieldsNullRoles()
    {
        var subject = await NewResolver(new InMemoryScimGroupStore(), new WritableScimGroupRoleMappingStore())
            .BuildSubjectAsync(User("u1"), client: null);

        Assert.Null(subject.Roles);
    }
}
