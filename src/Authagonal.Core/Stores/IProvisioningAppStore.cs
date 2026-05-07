using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IProvisioningAppStore
{
    Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default);
    Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default);
    Task DeleteAsync(string appId, CancellationToken ct = default);
}
