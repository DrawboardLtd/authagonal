namespace Authagonal.Core.Services;

/// <summary>
/// Provides per-request tenant context. In single-tenant deployments, returns
/// a fixed configuration. In multi-tenant (Cloud) deployments, resolved from
/// the Host header by TenantResolutionMiddleware.
/// </summary>
public interface ITenantContext
{
    string TenantId { get; }
    string Issuer { get; }
}
