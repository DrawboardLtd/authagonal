namespace Authagonal.Core.Services;

/// <summary>
/// Provides the list of downstream apps that users should be provisioned into.
/// The library provides a config-based default; multi-tenant hosts override with per-tenant state.
/// </summary>
public interface IProvisioningAppProvider
{
    Task<IReadOnlyList<ProvisioningApp>> GetAppsAsync(CancellationToken ct = default);
}

/// <summary>
/// A downstream app that participates in user provisioning via TCC (try/confirm/cancel).
/// </summary>
public sealed record ProvisioningApp(string AppId, string CallbackUrl, string? ApiKey);
