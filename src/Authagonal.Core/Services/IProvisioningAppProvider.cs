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
/// <param name="TryTimeoutSeconds">
/// Maximum time to wait for the /try callback. Null falls back to the
/// orchestrator default (60s). Raise this when the downstream app does real
/// work during Try (e.g. a routing slip that spins up an organization).
/// Confirm/Cancel/Deprovision always use a short fixed timeout and are not
/// tunable — they should always be cheap.
/// </param>
public sealed record ProvisioningApp(
    string AppId,
    string CallbackUrl,
    string? ApiKey,
    int? TryTimeoutSeconds = null);
