using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Configuration;
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
    IConfiguration configuration,
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

        // The administrative scope is unholdable by any client — see AdminScopeReservation. The
        // Server's own seeder enforces that; this one wrote AllowedScopes verbatim, and AddAuthagonal
        // registers THIS seeder inside every Server host too, so a host that binds
        // AuthagonalProtocolOptions from configuration had a second, unguarded route to the same
        // store. A seeded "authagonal-admin" client is permanent admin persistence.
        var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;

        foreach (var descriptor in protocolOptions.Clients)
        {
            // Skip the whole entry rather than silently dropping the scope: a seed asking for this is
            // a misconfiguration the operator needs to see, not one to paper over.
            if (AdminScopeReservation.Grants(descriptor.AllowedScopes, adminScope))
            {
                logger.LogError(
                    "Refusing to seed OIDC client {ClientId}: it requests the reserved administrative scope " +
                    "'{Scope}'. No client may hold it — a client that did could mint admin tokens indefinitely.",
                    descriptor.ClientId, adminScope);
                continue;
            }

            // A scope entry containing whitespace is not one scope: it expands into several in the
            // space-delimited `scope` claim, which is exactly how the reservation above gets bypassed.
            if (AdminScopeReservation.FindMalformedScope(descriptor.AllowedScopes) is { } malformed)
            {
                logger.LogError(
                    "Refusing to seed OIDC client {ClientId}: scope entry '{Scope}' is not a single scope " +
                    "token. Scope names cannot contain whitespace — list each scope separately.",
                    descriptor.ClientId, malformed);
                continue;
            }

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
