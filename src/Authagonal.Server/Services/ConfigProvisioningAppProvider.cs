using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default IProvisioningAppProvider that reads from IConfiguration.
/// Apps are configured under ProvisioningApps:{appId}:CallbackUrl and ProvisioningApps:{appId}:ApiKey.
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
