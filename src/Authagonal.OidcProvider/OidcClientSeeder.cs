using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Authagonal.OidcProvider;

/// <summary>
/// Seeds the <see cref="OidcClientDescriptor"/> entries from
/// <see cref="AuthagonalOidcProviderOptions"/> into OpenIddict's application store on
/// startup. Idempotent — re-runs overwrite the stored descriptor to match config.
/// </summary>
internal sealed class OidcClientSeeder(
    IServiceProvider services,
    IOptions<AuthagonalOidcProviderOptions> options,
    ILogger<OidcClientSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var clients = options.Value.Clients;
        if (clients.Count == 0)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in clients)
        {
            var descriptor = ToDescriptor(client);
            var existing = await manager.FindByClientIdAsync(client.ClientId, ct);
            if (existing is null)
            {
                await manager.CreateAsync(descriptor, ct);
                logger.LogInformation("Registered OIDC client {ClientId}", client.ClientId);
            }
            else
            {
                await manager.UpdateAsync(existing, descriptor, ct);
                logger.LogInformation("Updated OIDC client {ClientId}", client.ClientId);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static OpenIddictApplicationDescriptor ToDescriptor(OidcClientDescriptor c)
    {
        var d = new OpenIddictApplicationDescriptor
        {
            ClientId = c.ClientId,
            ClientSecret = c.ClientSecret,
            ClientType = string.IsNullOrEmpty(c.ClientSecret)
                ? OpenIddictConstants.ClientTypes.Public
                : OpenIddictConstants.ClientTypes.Confidential,
            DisplayName = string.IsNullOrEmpty(c.DisplayName) ? c.ClientId : c.DisplayName,
        };

        foreach (var uri in c.RedirectUris)
        {
            d.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }
        foreach (var uri in c.PostLogoutRedirectUris)
        {
            d.PostLogoutRedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        d.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
        d.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        d.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
        d.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
        d.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Profile);
        d.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Email);

        if (c.AllowRefreshToken)
        {
            d.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
        }

        foreach (var scope in c.AllowedScopes)
        {
            d.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scope);
        }

        if (c.RequirePkce)
        {
            d.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        }

        return d;
    }
}
