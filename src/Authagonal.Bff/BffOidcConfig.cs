using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Authagonal.Bff;

/// <summary>Discovers and caches the Authagonal tenant's OIDC metadata (endpoints + signing keys),
/// refreshing on the usual schedule and on signing-key rotation. Singleton per BFF.</summary>
public sealed class BffOidcConfig
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;

    public BffOidcConfig(IOptions<AuthagonalBffOptions> options)
    {
        var authority = options.Value.Authority.TrimEnd('/');
        var metadataAddress = $"{authority}/.well-known/openid-configuration";
        var requireHttps = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = requireHttps });
    }

    /// <summary>The current OIDC configuration (cached; refreshed automatically).</summary>
    public Task<OpenIdConnectConfiguration> GetAsync(CancellationToken ct = default)
        => _manager.GetConfigurationAsync(ct);
}
