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

    /// <summary>
    /// Sub-tenant environment (e.g. <c>live</c>, <c>test1</c>, <c>staging</c>).
    /// Live data lives in unprefixed tables (<c>{slug}-Users</c>); non-live envs
    /// share a per-tenant sandbox table set (<c>{slug}-sandbox-Users</c>) with
    /// the env discriminating rows via PartitionKey prefix. Defaults to <c>live</c>.
    /// </summary>
    string Env => LiveEnv;

    /// <summary>Canonical name for the live (production) environment.</summary>
    public const string LiveEnv = "live";

    /// <summary>
    /// Whether the login endpoint rejects users whose email is not yet confirmed.
    /// Defaults to <c>true</c> — an unconfirmed user cannot sign in. Multi-tenant
    /// deployments override this to <c>false</c> for control-plane (admin) tenants,
    /// where a freshly provisioned owner must be able to sign in and verify from
    /// inside the portal (the in-app verify banner, not a login wall).
    /// </summary>
    bool RequireConfirmedEmailForLogin => true;
}
