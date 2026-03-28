using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class ProviderSeedService(
    ISamlProviderStore samlStore,
    IOidcProviderStore oidcStore,
    ISsoDomainStore ssoDomainStore,
    ISecretProvider secretProvider,
    IConfiguration configuration,
    ILogger<ProviderSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await SeedSamlProviders(ct);
        await SeedOidcProviders(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task SeedSamlProviders(CancellationToken ct)
    {
        var providers = configuration.GetSection("SamlProviders").Get<List<SamlProviderSeed>>();
        if (providers is null || providers.Count == 0)
            return;

        foreach (var seed in providers)
        {
            if (string.IsNullOrWhiteSpace(seed.ConnectionId))
            {
                logger.LogWarning("Skipping SAML provider seed entry with missing ConnectionId");
                continue;
            }

            var config = new SamlProviderConfig
            {
                ConnectionId = seed.ConnectionId,
                ConnectionName = seed.ConnectionName ?? seed.ConnectionId,
                EntityId = seed.EntityId ?? throw new InvalidOperationException(
                    $"SAML provider '{seed.ConnectionId}' is missing required EntityId"),
                MetadataLocation = seed.MetadataLocation ?? throw new InvalidOperationException(
                    $"SAML provider '{seed.ConnectionId}' is missing required MetadataLocation"),
                AllowedDomains = seed.AllowedDomains ?? [],
                CreatedAt = DateTimeOffset.UtcNow
            };

            await samlStore.UpsertAsync(config, ct);

            foreach (var domain in config.AllowedDomains)
            {
                await ssoDomainStore.UpsertAsync(new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "saml",
                    ConnectionId = config.ConnectionId,
                    Scheme = $"saml-{config.ConnectionId}"
                }, ct);
            }

            logger.LogInformation("Seeded SAML provider {Id} ({Name})", config.ConnectionId, config.ConnectionName);
        }
    }

    private async Task SeedOidcProviders(CancellationToken ct)
    {
        var providers = configuration.GetSection("OidcProviders").Get<List<OidcProviderSeed>>();
        if (providers is null || providers.Count == 0)
            return;

        foreach (var seed in providers)
        {
            if (string.IsNullOrWhiteSpace(seed.ConnectionId))
            {
                logger.LogWarning("Skipping OIDC provider seed entry with missing ConnectionId");
                continue;
            }

            var protectedSecret = await secretProvider.ProtectAsync(
                $"oidc-{seed.ConnectionId}-client-secret",
                seed.ClientSecret ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required ClientSecret"),
                ct);

            var config = new OidcProviderConfig
            {
                ConnectionId = seed.ConnectionId,
                ConnectionName = seed.ConnectionName ?? seed.ConnectionId,
                MetadataLocation = seed.MetadataLocation ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required MetadataLocation"),
                ClientId = seed.ClientId ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required ClientId"),
                ClientSecret = protectedSecret,
                RedirectUrl = seed.RedirectUrl ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required RedirectUrl"),
                AllowedDomains = seed.AllowedDomains ?? [],
                CreatedAt = DateTimeOffset.UtcNow
            };

            await oidcStore.UpsertAsync(config, ct);

            foreach (var domain in config.AllowedDomains)
            {
                await ssoDomainStore.UpsertAsync(new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "oidc",
                    ConnectionId = config.ConnectionId,
                    Scheme = $"oidc-{config.ConnectionId}"
                }, ct);
            }

            logger.LogInformation("Seeded OIDC provider {Id} ({Name})", config.ConnectionId, config.ConnectionName);
        }
    }

    public sealed class SamlProviderSeed
    {
        public string? ConnectionId { get; set; }
        public string? ConnectionName { get; set; }
        public string? EntityId { get; set; }
        public string? MetadataLocation { get; set; }
        public List<string>? AllowedDomains { get; set; }
    }

    public sealed class OidcProviderSeed
    {
        public string? ConnectionId { get; set; }
        public string? ConnectionName { get; set; }
        public string? MetadataLocation { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RedirectUrl { get; set; }
        public List<string>? AllowedDomains { get; set; }
    }
}
