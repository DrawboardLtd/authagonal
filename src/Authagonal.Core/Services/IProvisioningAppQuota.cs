namespace Authagonal.Core.Services;

/// <summary>
/// Optional quota gate for provisioning-app creation. Hosts that limit how many
/// provisioning apps a single tenant can register (e.g. SaaS tier caps) implement
/// this; single-tenant OSS deployments accept the default no-quota implementation.
/// </summary>
public interface IProvisioningAppQuota
{
    /// <summary>
    /// Returns the maximum number of provisioning apps allowed, or null for unlimited.
    /// Called at create time before <see cref="Stores.IProvisioningAppStore.UpsertAsync"/>.
    /// </summary>
    Task<int?> GetMaxAsync(CancellationToken ct = default);
}

/// <summary>
/// Default no-quota implementation. Always allows creation.
/// </summary>
public sealed class UnlimitedProvisioningAppQuota : IProvisioningAppQuota
{
    public Task<int?> GetMaxAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
}
