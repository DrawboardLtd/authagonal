using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Hosted service that seeds <see cref="OidcClientDescriptor"/> entries and
/// <see cref="OidcScopeDescriptor"/> entries from <see cref="AuthagonalProtocolOptions"/>
/// into the host's <see cref="IClientStore"/> and <see cref="IScopeStore"/> on startup.
/// Idempotent — re-upserts every run so configuration changes (new redirect URIs, updated
/// secrets) are picked up on restart.
/// </summary>
internal sealed class ProtocolSeedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthagonalProtocolOptions> options,
    ILogger<ProtocolSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var protocolOptions = options.Value;
        if (protocolOptions.Clients.Count == 0 && protocolOptions.Scopes.Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var clientStore = scope.ServiceProvider.GetRequiredService<IClientStore>();
        var scopeStore = scope.ServiceProvider.GetRequiredService<IScopeStore>();

        foreach (var descriptor in protocolOptions.Clients)
        {
            var client = ToOAuthClient(descriptor);
            await clientStore.UpsertAsync(client, cancellationToken);
            logger.LogInformation("Seeded OIDC client {ClientId}", descriptor.ClientId);
        }

        foreach (var descriptor in protocolOptions.Scopes)
        {
            var existing = await scopeStore.GetAsync(descriptor.Name, cancellationToken);
            var scopeEntity = new Scope
            {
                Name = descriptor.Name,
                DisplayName = descriptor.DisplayName ?? descriptor.Name,
                ShowInDiscoveryDocument = descriptor.ShowInDiscoveryDocument,
                UserClaims = descriptor.UserClaims,
            };
            if (existing is null)
                await scopeStore.CreateAsync(scopeEntity, cancellationToken);
            else
                await scopeStore.UpdateAsync(scopeEntity, cancellationToken);
            logger.LogInformation("Seeded OIDC scope {ScopeName}", descriptor.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static OAuthClient ToOAuthClient(OidcClientDescriptor d)
    {
        var grantTypes = new List<string> { "authorization_code" };
        if (d.AllowRefreshToken) grantTypes.Add("refresh_token");

        var secretHashes = new List<string>();
        if (!string.IsNullOrEmpty(d.ClientSecret))
            secretHashes.Add(BCrypt.Net.BCrypt.HashPassword(d.ClientSecret));

        return new OAuthClient
        {
            ClientId = d.ClientId,
            ClientName = string.IsNullOrEmpty(d.DisplayName) ? d.ClientId : d.DisplayName,
            Enabled = true,
            ClientSecretHashes = secretHashes,
            AllowedGrantTypes = grantTypes,
            RedirectUris = d.RedirectUris,
            PostLogoutRedirectUris = d.PostLogoutRedirectUris,
            Audiences = d.Audiences,
            AllowedScopes = d.AllowedScopes,
            RequirePkce = d.RequirePkce,
            AllowOfflineAccess = d.AllowRefreshToken,
            RequireClientSecret = d.RequireClientSecret,
            RequireConsent = d.RequireConsent,
            AccessTokenLifetimeSeconds = d.AccessTokenLifetimeSeconds,
            IdentityTokenLifetimeSeconds = d.IdentityTokenLifetimeSeconds,
            AuthorizationCodeLifetimeSeconds = d.AuthorizationCodeLifetimeSeconds,
            AbsoluteRefreshTokenLifetimeSeconds = d.AbsoluteRefreshTokenLifetimeSeconds,
            SlidingRefreshTokenLifetimeSeconds = d.SlidingRefreshTokenLifetimeSeconds,
            RefreshTokenExpiration = RefreshTokenExpiration.Absolute,
            RefreshTokenUsage = RefreshTokenUsage.OneTime,
        };
    }
}
