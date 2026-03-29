using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class ClientSeedService(
    IClientStore clientStore,
    PasswordHasher passwordHasher,
    IConfiguration configuration,
    ILogger<ClientSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var clients = configuration.GetSection("Clients").Get<List<ClientSeedConfig>>() ?? [];

        // Also support a single "SeedClient" section for simple demos
        var singleClient = configuration.GetSection("SeedClient").Get<ClientSeedConfig>();
        if (singleClient is not null && !string.IsNullOrWhiteSpace(singleClient.EffectiveId))
        {
            clients.Add(singleClient);
        }

        if (clients.Count == 0)
        {
            logger.LogDebug("No client seed configuration found");
            return;
        }

        foreach (var seed in clients)
        {
            var clientId = seed.EffectiveId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                logger.LogWarning("Skipping client seed entry with missing Id");
                continue;
            }

            // Build secret hashes: use explicit hashes if provided, otherwise hash plaintext secret
            var secretHashes = seed.SecretHashes ?? [];
            if (secretHashes.Count == 0 && !string.IsNullOrWhiteSpace(seed.ClientSecret))
            {
                secretHashes = [passwordHasher.HashPassword(seed.ClientSecret)];
            }

            var client = new OAuthClient
            {
                ClientId = clientId,
                ClientName = seed.Name ?? seed.ClientName ?? clientId,
                ClientSecretHashes = secretHashes,
                AllowedGrantTypes = seed.GrantTypes ?? seed.AllowedGrantTypes ?? [],
                RedirectUris = seed.RedirectUris ?? [],
                PostLogoutRedirectUris = seed.PostLogoutRedirectUris ?? [],
                AllowedScopes = seed.Scopes ?? seed.AllowedScopes ?? [],
                AllowedCorsOrigins = seed.CorsOrigins ?? seed.AllowedCorsOrigins ?? [],
                RequirePkce = seed.RequirePkce ?? true,
                AllowOfflineAccess = seed.AllowOfflineAccess ?? false,
                RequireClientSecret = seed.RequireSecret ?? seed.RequireClientSecret ?? true,
                AlwaysIncludeUserClaimsInIdToken = seed.AlwaysIncludeUserClaimsInIdToken ?? false,
                AccessTokenLifetimeSeconds = seed.AccessTokenLifetimeSeconds ?? 1800,
                IdentityTokenLifetimeSeconds = seed.IdentityTokenLifetimeSeconds ?? 300,
                AuthorizationCodeLifetimeSeconds = seed.AuthorizationCodeLifetimeSeconds ?? 300,
                AbsoluteRefreshTokenLifetimeSeconds = seed.AbsoluteRefreshTokenLifetimeSeconds ?? 2592000,
                SlidingRefreshTokenLifetimeSeconds = seed.SlidingRefreshTokenLifetimeSeconds ?? 1296000,
                RefreshTokenUsage = seed.RefreshTokenUsage ?? RefreshTokenUsage.OneTime,
                MfaPolicy = seed.MfaPolicy ?? MfaPolicy.Disabled
            };

            await clientStore.UpsertAsync(client, ct);
            logger.LogInformation("Seeded client {Id} ({Name})", client.ClientId, client.ClientName);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public sealed class ClientSeedConfig
    {
        // Client identity — supports both "Id" (compact) and "ClientId" (demo-friendly)
        public string? Id { get; set; }
        public string? ClientId { get; set; }
        internal string? EffectiveId => ClientId ?? Id;

        public string? Name { get; set; }
        public string? ClientName { get; set; }

        // Secrets — either pre-hashed or plaintext (auto-hashed on startup)
        public List<string>? SecretHashes { get; set; }
        public string? ClientSecret { get; set; }

        // Grant types — "GrantTypes" (compact) or "AllowedGrantTypes" (demo-friendly)
        public List<string>? GrantTypes { get; set; }
        public List<string>? AllowedGrantTypes { get; set; }

        public List<string>? RedirectUris { get; set; }
        public List<string>? PostLogoutRedirectUris { get; set; }

        // Scopes — "Scopes" (compact) or "AllowedScopes" (demo-friendly)
        public List<string>? Scopes { get; set; }
        public List<string>? AllowedScopes { get; set; }

        // CORS — "CorsOrigins" (compact) or "AllowedCorsOrigins" (demo-friendly)
        public List<string>? CorsOrigins { get; set; }
        public List<string>? AllowedCorsOrigins { get; set; }

        public bool? RequirePkce { get; set; }
        public bool? AllowOfflineAccess { get; set; }
        public bool? RequireSecret { get; set; }
        public bool? RequireClientSecret { get; set; }
        public bool? AlwaysIncludeUserClaimsInIdToken { get; set; }
        public int? AccessTokenLifetimeSeconds { get; set; }
        public int? IdentityTokenLifetimeSeconds { get; set; }
        public int? AuthorizationCodeLifetimeSeconds { get; set; }
        public int? AbsoluteRefreshTokenLifetimeSeconds { get; set; }
        public int? SlidingRefreshTokenLifetimeSeconds { get; set; }
        public RefreshTokenUsage? RefreshTokenUsage { get; set; }
        public MfaPolicy? MfaPolicy { get; set; }
    }
}
