using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IProvisioningAppStore
{
    Task<ProvisioningAppConfig?> GetAsync(string appId, CancellationToken ct = default);
    Task<IReadOnlyList<ProvisioningAppConfig>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(ProvisioningAppConfig app, CancellationToken ct = default);
    Task DeleteAsync(string appId, CancellationToken ct = default);

    /// <summary>
    /// Re-encrypt legacy plaintext <c>ApiKey</c> values at rest (the outbound Bearer credential the
    /// provisioning orchestrator sends to a callback — reversible, so encrypted not hashed). Table scan,
    /// legacy-selective (already-encrypted rows skipped), idempotent, write-in-place. Returns rows found
    /// (<paramref name="dryRun"/>) / migrated (live run). Default: no-op (non-encrypting stores).
    /// </summary>
    Task<int> MigrateProvisioningAppsAsync(bool dryRun, CancellationToken ct = default) => Task.FromResult(0);
}
