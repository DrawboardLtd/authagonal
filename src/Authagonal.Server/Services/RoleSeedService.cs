using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Seeds roles from the <c>Roles</c> config section at startup, and optionally puts named people in
/// them, mirroring <see cref="ClientSeedService"/> and <see cref="ScopeSeedService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Roles were the one thing every other kind of registration could be seeded but this could not, and
/// <see cref="Scope.AllowedRoles"/> is what made that a real gap: gating a scope on a role nothing
/// creates gates it against everybody, including the operator who configured it. A fresh environment
/// now comes up with its roles present and at least one person in each, exactly as it comes up with
/// its clients and scopes.
/// </para>
/// <para>
/// Members are matched by email and skipped — with a warning, not an error — when no such user exists
/// yet. Boot must not depend on an account that has not been created; the next boot picks them up.
/// Idempotent throughout: running it again neither duplicates a role nor re-adds a member.
/// </para>
/// <para>
/// Deliberately additive. It never removes a role or revokes a membership, because config is not the
/// system of record for who holds what — an operator granting a role through the admin API must not
/// have it taken away by the next restart.
/// </para>
/// </remarks>
public sealed class RoleSeedService(
    IRoleStore roleStore,
    IUserStore userStore,
    IConfiguration configuration,
    ILogger<RoleSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var roles = configuration.GetSection("Roles").Get<List<RoleSeedConfig>>() ?? [];
        if (roles.Count == 0)
        {
            logger.LogDebug("No role seed configuration found");
            return;
        }

        foreach (var seed in roles)
        {
            if (string.IsNullOrWhiteSpace(seed.Name))
            {
                logger.LogWarning("Skipping role seed entry with missing Name");
                continue;
            }

            var role = await EnsureRoleAsync(seed, ct);
            if (role is null) continue;

            foreach (var rawEmail in seed.Members ?? [])
            {
                await EnsureMemberAsync(role.Name, rawEmail, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<Role?> EnsureRoleAsync(RoleSeedConfig seed, CancellationToken ct)
    {
        var existing = await roleStore.GetByNameAsync(seed.Name!, ct);
        if (existing is not null)
        {
            // Description is the only mutable field, and only when the seed states one — the same
            // config-wins-where-it-speaks rule the scope seeder follows.
            if (!string.IsNullOrWhiteSpace(seed.Description) && seed.Description != existing.Description)
            {
                existing.Description = seed.Description;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await roleStore.UpdateAsync(existing, ct);
            }
            return existing;
        }

        var role = new Role
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = seed.Name!,
            Description = seed.Description,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await roleStore.CreateAsync(role, ct);
        logger.LogInformation("Seeded role {Name}", role.Name);
        return role;
    }

    private async Task EnsureMemberAsync(string roleName, string rawEmail, CancellationToken ct)
    {
        var email = rawEmail?.Trim();
        if (string.IsNullOrEmpty(email)) return;

        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
        {
            logger.LogWarning("Seed member {Email} for role {Role} not found — skipping (retried on next boot)",
                email, roleName);
            return;
        }

        if (user.Roles.Contains(roleName, StringComparer.Ordinal)) return;

        user.Roles.Add(roleName);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.UpdateAsync(user, ct);
        logger.LogInformation("Added seed member {Email} to role {Role}", email, roleName);
    }

    public sealed class RoleSeedConfig
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        /// <summary>Emails to place in this role on every boot. Unknown addresses are skipped.</summary>
        public List<string>? Members { get; set; }
    }
}
