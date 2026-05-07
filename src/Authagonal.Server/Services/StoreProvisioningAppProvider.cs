using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Default <see cref="IProvisioningAppProvider"/> backed by <see cref="IProvisioningAppStore"/>.
/// Projects the persisted <c>ProvisioningAppConfig</c> rows down to the
/// orchestrator-facing <see cref="ProvisioningApp"/> value type.
/// </summary>
public sealed class StoreProvisioningAppProvider(IProvisioningAppStore store) : IProvisioningAppProvider
{
    public async Task<IReadOnlyList<ProvisioningApp>> GetAppsAsync(CancellationToken ct = default)
    {
        var apps = await store.GetAllAsync(ct);
        return apps
            .Select(a => new ProvisioningApp(a.AppId, a.CallbackUrl, a.ApiKey, a.TryTimeoutSeconds))
            .ToList();
    }
}
