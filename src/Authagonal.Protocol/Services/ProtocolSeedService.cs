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
            var existing = await clientStore.GetAsync(descriptor.ClientId, cancellationToken);
            await clientStore.UpsertAsync(ApplySeed(existing, descriptor), cancellationToken);
            logger.LogInformation("Seeded OIDC client {ClientId}", descriptor.ClientId);
        }

        foreach (var descriptor in protocolOptions.Scopes)
        {
            var existing = await scopeStore.GetAsync(descriptor.Name, cancellationToken);
            var scopeEntity = new Scope
            {
                Name = descriptor.Name,
                DisplayName = descriptor.DisplayName ?? descriptor.Name,
                // Carried through to the store, and from there to the consent screen. These were
                // silently dropped: Scope has held Description/Emphasize/Required all along, but seeding
                // never populated them, so no amount of configuration could reach the consent UI.
                Description = descriptor.Description,
                Emphasize = descriptor.Emphasize,
                Group = descriptor.Group,
                Required = descriptor.Required,
                ShowInDiscoveryDocument = descriptor.ShowInDiscoveryDocument,
                UserClaims = descriptor.UserClaims,

                // Preserved from the stored scope, because the seed descriptor has no field for it.
                //
                // A fresh Scope defaults AllowedRoles to empty, and this seeder wrote that over the
                // stored row on every restart — silently clearing the per-user entitlement gate an
                // admin had configured through the scope API. An empty AllowedRoles means "ungated",
                // so a restart turned a role-restricted scope into one every user could obtain, and
                // nothing recorded that it had happened.
                AllowedRoles = existing?.AllowedRoles ?? [],
            };
            if (existing is null)
                await scopeStore.CreateAsync(scopeEntity, cancellationToken);
            else
                await scopeStore.UpdateAsync(scopeEntity, cancellationToken);
            logger.LogInformation("Seeded OIDC scope {ScopeName}", descriptor.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Read-merge-write, matching the Server host's <c>ClientSeedService</c> and
    /// <c>ScopeSeedService</c>: the seed states what it states, and every other field keeps the
    /// stored value.
    /// </summary>
    /// <remarks>
    /// This seeder used to build a fresh <see cref="OAuthClient"/> and upsert it over the stored row
    /// on every pod start, so an embedder that configures <c>AuthagonalProtocolOptions.Clients</c> had
    /// each restart revert everything the descriptor has no field for back to the model default.
    /// <c>Enabled = true</c> was hard-set, so a client an operator deliberately disabled came back up
    /// enabled; <c>RequirePushedAuthorizationRequests</c>, <c>JwksJson</c>/<c>JwksUri</c> (whose loss
    /// kills <c>private_key_jwt</c> for an agent client), <c>AllowedCorsOrigins</c> and
    /// <c>FrontChannelLogoutUri</c> were all silently cleared. The Server host was fixed for exactly
    /// this and its sibling was left behind.
    /// </remarks>
    private static OAuthClient ApplySeed(OAuthClient? existing, OidcClientDescriptor d)
    {
        var client = existing ?? new OAuthClient { ClientId = d.ClientId };

        var grantTypes = new List<string> { "authorization_code" };
        if (d.AllowRefreshToken) grantTypes.Add("refresh_token");

        client.ClientId = d.ClientId;
        client.ClientName = string.IsNullOrEmpty(d.DisplayName) ? d.ClientId : d.DisplayName;

        // Only overwritten when the descriptor actually carries a secret, so a rotation applied
        // through the admin API survives the next restart. Enabled is not written at all.
        if (!string.IsNullOrEmpty(d.ClientSecret))
            client.ClientSecretHashes = [BCrypt.Net.BCrypt.HashPassword(d.ClientSecret)];

        client.AllowedGrantTypes = grantTypes;
        client.RedirectUris = d.RedirectUris;
        client.PostLogoutRedirectUris = d.PostLogoutRedirectUris;
        client.Audiences = d.Audiences;
        client.AllowedScopes = d.AllowedScopes;
        client.RequirePkce = d.RequirePkce;
        client.AllowOfflineAccess = d.AllowRefreshToken;
        client.RequireClientSecret = d.RequireClientSecret;
        client.RequireConsent = d.RequireConsent;
        client.AccessTokenLifetimeSeconds = d.AccessTokenLifetimeSeconds;
        client.IdentityTokenLifetimeSeconds = d.IdentityTokenLifetimeSeconds;
        client.AuthorizationCodeLifetimeSeconds = d.AuthorizationCodeLifetimeSeconds;
        client.AbsoluteRefreshTokenLifetimeSeconds = d.AbsoluteRefreshTokenLifetimeSeconds;
        client.SlidingRefreshTokenLifetimeSeconds = d.SlidingRefreshTokenLifetimeSeconds;

        // The descriptor has no field for these two, so they are the seeder's opinion for a client it
        // is CREATING — an existing row keeps whatever an operator set.
        if (existing is null)
        {
            client.RefreshTokenExpiration = RefreshTokenExpiration.Absolute;
            client.RefreshTokenUsage = RefreshTokenUsage.OneTime;
        }

        return client;
    }
}
