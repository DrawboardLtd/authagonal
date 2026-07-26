using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Seeds custom scopes from the <c>Scopes</c> config section at startup, mirroring
/// <see cref="ClientSeedService"/>. Registering a scope makes it appear in the discovery document's
/// <c>scopes_supported</c> and lets it release custom user claims (<c>UserClaims</c>) onto tokens.
/// Config wins for any field the seed SETS: an existing scope with the same name is updated to the
/// seeded values. A field omitted from the seed (null) preserves the stored value — config can add or
/// update a field but cannot CLEAR one (e.g. emptying <c>UserClaims</c> means editing the scope directly).
/// </summary>
public sealed class ScopeSeedService(
    IScopeStore scopeStore,
    IConfiguration configuration,
    ILogger<ScopeSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var scopes = configuration.GetSection("Scopes").Get<List<ScopeSeedConfig>>() ?? [];
        if (scopes.Count == 0)
        {
            logger.LogDebug("No scope seed configuration found");
            return;
        }

        foreach (var seed in scopes)
        {
            if (string.IsNullOrWhiteSpace(seed.Name))
            {
                logger.LogWarning("Skipping scope seed entry with missing Name");
                continue;
            }

            var existing = await scopeStore.GetAsync(seed.Name, ct);
            var scope = existing ?? new Scope { Name = seed.Name, CreatedAt = DateTimeOffset.UtcNow };
            scope.DisplayName = seed.DisplayName ?? scope.DisplayName;
            scope.Description = seed.Description ?? scope.Description;
            scope.UserClaims = seed.UserClaims ?? scope.UserClaims;
            scope.ShowInDiscoveryDocument = seed.ShowInDiscoveryDocument ?? scope.ShowInDiscoveryDocument;
            scope.Emphasize = seed.Emphasize ?? scope.Emphasize;
            scope.Group = seed.Group ?? scope.Group;
            scope.Required = seed.Required ?? scope.Required;

            if (existing is null)
                await scopeStore.CreateAsync(scope, ct);
            else
            {
                scope.UpdatedAt = DateTimeOffset.UtcNow;
                await scopeStore.UpdateAsync(scope, ct);
            }
            logger.LogInformation("Seeded scope {Name}", scope.Name);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public sealed class ScopeSeedConfig
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        /// <summary>User claim types this scope releases onto tokens (e.g. org_role, workspace_id).</summary>
        public List<string>? UserClaims { get; set; }
        public bool? ShowInDiscoveryDocument { get; set; }

        /// <summary>Draws attention to the scope on the consent screen — use it for the ones that write.</summary>
        public bool? Emphasize { get; set; }

        /// <summary>
        /// A heading to file this scope under on the consent screen. Omitted leaves whatever is stored.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>The user may not decline the scope; consent shows it ticked and locked.</summary>
        public bool? Required { get; set; }
    }
}
