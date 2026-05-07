using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default <see cref="IProvisioningAppProvider"/> for hosts that drive provisioning
/// apps from <see cref="IConfiguration"/> and don't need runtime mutation. Apps are
/// configured under <c>ProvisioningApps:{appId}:CallbackUrl</c> /
/// <c>:ApiKey</c> / <c>:TryTimeoutSeconds</c>.
///
/// Hosts that want runtime CRUD via <c>/api/v1/provisioning/apps</c> register
/// <see cref="StoreProvisioningAppProvider"/> explicitly (it reads from
/// <see cref="Authagonal.Core.Stores.IProvisioningAppStore"/>); since this default
/// is registered with <c>TryAdd</c>, the explicit registration wins.
/// </summary>
public sealed class ConfigProvisioningAppProvider(IConfiguration configuration) : IProvisioningAppProvider
{
    public Task<IReadOnlyList<ProvisioningApp>> GetAppsAsync(CancellationToken ct = default)
    {
        var section = configuration.GetSection("ProvisioningApps");
        var apps = new List<ProvisioningApp>();

        foreach (var child in section.GetChildren())
        {
            var callbackUrl = child["CallbackUrl"];
            if (string.IsNullOrWhiteSpace(callbackUrl)) continue;

            var tryTimeout = int.TryParse(child["TryTimeoutSeconds"], out var t) ? t : (int?)null;
            apps.Add(new ProvisioningApp(child.Key, callbackUrl, child["ApiKey"], tryTimeout));
        }

        return Task.FromResult<IReadOnlyList<ProvisioningApp>>(apps);
    }
}
