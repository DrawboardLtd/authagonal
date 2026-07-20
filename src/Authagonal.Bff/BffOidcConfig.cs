using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Authagonal.Bff;

/// <summary>Discovers and caches each tenant's OIDC metadata (endpoints + signing keys), keyed by authority,
/// refreshing on the usual schedule and on signing-key rotation. One <see cref="ConfigurationManager{T}"/> per
/// authority — so a single multi-tenant BFF discovers each tenant's auth host independently. Singleton.</summary>
public sealed class BffOidcConfig
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current OIDC configuration for the given authority (cached; refreshed automatically).</summary>
    public Task<OpenIdConnectConfiguration> GetAsync(string authority, CancellationToken ct = default)
        => _managers.GetOrAdd(authority.TrimEnd('/'), static a =>
        {
            var metadataAddress = $"{a}/.well-known/openid-configuration";
            var requireHttps = a.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = requireHttps });
        }).GetConfigurationAsync(ct);
}
