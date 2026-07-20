using Microsoft.Extensions.Options;

namespace Authagonal.Bff;

/// <summary>The default resolver: single-tenant. Always returns the one tenant configured in
/// <see cref="AuthagonalBffOptions"/>, ignoring the tenant key — so existing single-tenant consumers behave
/// exactly as before the multi-tenant seam was added. Registered via <c>TryAdd</c>, so a host that registers
/// its own <see cref="IBffTenantResolver"/> replaces it.</summary>
internal sealed class StaticBffTenantResolver : IBffTenantResolver
{
    private readonly BffTenantConfig _config;

    public StaticBffTenantResolver(IOptions<AuthagonalBffOptions> options)
    {
        var o = options.Value;
        _config = new BffTenantConfig
        {
            TenantKey = null,
            Authority = o.Authority.TrimEnd('/'),
            ClientId = o.ClientId,
            ClientSecret = o.ClientSecret,
            Scope = o.Scope.ToArray(),
        };
    }

    public Task<BffTenantConfig?> ResolveAsync(string? tenantKey, CancellationToken ct = default)
        => Task.FromResult<BffTenantConfig?>(_config);

    // There is only one tenant; a back-channel token for any other issuer will fail signature/audience
    // validation against this config's JWKS + client id, so returning it unconditionally is safe.
    public Task<BffTenantConfig?> ResolveByIssuerAsync(string issuer, CancellationToken ct = default)
        => Task.FromResult<BffTenantConfig?>(_config);
}
