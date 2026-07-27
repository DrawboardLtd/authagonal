using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Authagonal.Tests;

/// <summary>
/// Role seeding: a fresh environment comes up with its roles present and its named people in them.
/// </summary>
/// <remarks>
/// This exists because <see cref="Scope.AllowedRoles"/> made the absence load-bearing. A scope gated
/// on a role that nothing creates is gated against everybody — including the operator who configured
/// it — and the failure is silent: the scope is simply never granted, and the person is simply never
/// told why.
/// </remarks>
public sealed class RoleSeedServiceTests
{
    [Fact]
    public async Task Seeds_RolesAndTheirMembers()
    {
        await using var factory = new AuthagonalTestFactory();
        var member = await factory.SeedTestUserAsync(email: "staff@example.com");

        await RunSeed(factory, """
            { "Roles": [ { "Name": "staff-admin", "Description": "Staff", "Members": [ "staff@example.com" ] } ] }
            """);

        var role = await factory.RoleStore.GetByNameAsync("staff-admin");
        Assert.NotNull(role);
        Assert.Equal("Staff", role!.Description);

        var seeded = await factory.UserStore.GetAsync(member.Id);
        Assert.Contains("staff-admin", seeded!.Roles);
    }

    [Fact]
    public async Task IsIdempotent_AcrossReboots()
    {
        await using var factory = new AuthagonalTestFactory();
        var member = await factory.SeedTestUserAsync(email: "staff@example.com");
        const string config = """
            { "Roles": [ { "Name": "staff-admin", "Members": [ "staff@example.com" ] } ] }
            """;

        await RunSeed(factory, config);
        await RunSeed(factory, config);

        Assert.Single(await factory.RoleStore.ListAsync(), r => r.Name == "staff-admin");
        var seeded = await factory.UserStore.GetAsync(member.Id);
        Assert.Single(seeded!.Roles, r => r == "staff-admin");
    }

    /// <summary>
    /// Boot must not depend on an account that does not exist yet — the bootstrap list names people,
    /// and people are created on their own schedule.
    /// </summary>
    [Fact]
    public async Task UnknownMember_IsSkippedRatherThanFailingBoot()
    {
        await using var factory = new AuthagonalTestFactory();

        await RunSeed(factory, """
            { "Roles": [ { "Name": "staff-admin", "Members": [ "nobody@example.com" ] } ] }
            """);

        // The role still lands; only the membership is deferred.
        Assert.NotNull(await factory.RoleStore.GetByNameAsync("staff-admin"));
    }

    /// <summary>
    /// Config is not the system of record for who holds what. A role granted through the admin API
    /// must survive the next restart, or every operator action is provisional.
    /// </summary>
    [Fact]
    public async Task DoesNotRevokeAMembershipItDidNotSeed()
    {
        await using var factory = new AuthagonalTestFactory();
        var member = await factory.SeedTestUserAsync(email: "staff@example.com");
        member.Roles.Add("granted-by-hand");
        await factory.UserStore.UpdateAsync(member);

        await RunSeed(factory, """
            { "Roles": [ { "Name": "staff-admin", "Members": [ "staff@example.com" ] } ] }
            """);

        var seeded = await factory.UserStore.GetAsync(member.Id);
        Assert.Contains("granted-by-hand", seeded!.Roles);
        Assert.Contains("staff-admin", seeded.Roles);
    }

    private static async Task RunSeed(AuthagonalTestFactory factory, string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var seeder = new RoleSeedService(
            factory.RoleStore,
            factory.UserStore,
            configuration,
            factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger<RoleSeedService>());

        await seeder.StartAsync(CancellationToken.None);
    }
}
