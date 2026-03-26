using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class ClientSeedService(
    IClientStore clientStore,
    IConfiguration configuration,
    ILogger<ClientSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var clients = configuration.GetSection("Clients").Get<List<ClientSeedConfig>>();
        if (clients is null || clients.Count == 0)
        {
            logger.LogDebug("No client seed configuration found");
            return;
        }

        foreach (var seed in clients)
        {
            if (string.IsNullOrWhiteSpace(seed.Id))
            {
                logger.LogWarning("Skipping client seed entry with missing Id");
                continue;
            }

            var client = new OAuthClient
            {
                ClientId = seed.Id,
                ClientName = seed.Name ?? seed.Id,
                ClientSecretHashes = seed.SecretHashes ?? [],
                AllowedGrantTypes = seed.GrantTypes ?? [],
                RedirectUris = seed.RedirectUris ?? [],
                PostLogoutRedirectUris = seed.PostLogoutRedirectUris ?? [],
                AllowedScopes = seed.Scopes ?? [],
                AllowedCorsOrigins = seed.CorsOrigins ?? [],
                RequirePkce = seed.RequirePkce ?? true,
                AllowOfflineAccess = seed.AllowOfflineAccess ?? false,
                RequireClientSecret = seed.RequireSecret ?? true,
                AlwaysIncludeUserClaimsInIdToken = seed.AlwaysIncludeUserClaimsInIdToken ?? false,
                AccessTokenLifetimeSeconds = seed.AccessTokenLifetimeSeconds ?? 1800,
                IdentityTokenLifetimeSeconds = seed.IdentityTokenLifetimeSeconds ?? 300,
                AuthorizationCodeLifetimeSeconds = seed.AuthorizationCodeLifetimeSeconds ?? 300,
                AbsoluteRefreshTokenLifetimeSeconds = seed.AbsoluteRefreshTokenLifetimeSeconds ?? 2592000,
                SlidingRefreshTokenLifetimeSeconds = seed.SlidingRefreshTokenLifetimeSeconds ?? 1296000,
                RefreshTokenUsage = seed.RefreshTokenUsage ?? RefreshTokenUsage.OneTime
            };

            await clientStore.UpsertAsync(client, ct);
            logger.LogInformation("Seeded client {Id} ({Name})", client.ClientId, client.ClientName);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public sealed class ClientSeedConfig
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string>? SecretHashes { get; set; }
        public List<string>? GrantTypes { get; set; }
        public List<string>? RedirectUris { get; set; }
        public List<string>? PostLogoutRedirectUris { get; set; }
        public List<string>? Scopes { get; set; }
        public List<string>? CorsOrigins { get; set; }
        public bool? RequirePkce { get; set; }
        public bool? AllowOfflineAccess { get; set; }
        public bool? RequireSecret { get; set; }
        public bool? AlwaysIncludeUserClaimsInIdToken { get; set; }
        public int? AccessTokenLifetimeSeconds { get; set; }
        public int? IdentityTokenLifetimeSeconds { get; set; }
        public int? AuthorizationCodeLifetimeSeconds { get; set; }
        public int? AbsoluteRefreshTokenLifetimeSeconds { get; set; }
        public int? SlidingRefreshTokenLifetimeSeconds { get; set; }
        public RefreshTokenUsage? RefreshTokenUsage { get; set; }
    }
}
