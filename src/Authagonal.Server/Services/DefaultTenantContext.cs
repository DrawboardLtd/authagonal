using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default single-tenant implementation of <see cref="ITenantContext"/>.
/// Reads the issuer from IConfiguration, suitable for standalone (non-Cloud) deployments.
/// </summary>
public sealed class DefaultTenantContext : ITenantContext
{
    public string TenantId => "default";
    public string Issuer { get; }

    public DefaultTenantContext(IConfiguration configuration)
    {
        Issuer = configuration["Issuer"]
            ?? throw new InvalidOperationException("Issuer is not configured");
    }
}
