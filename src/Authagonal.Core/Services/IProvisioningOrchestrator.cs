using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// Orchestrates user provisioning into downstream apps using TCC (Try-Confirm-Cancel).
/// </summary>
public interface IProvisioningOrchestrator
{
    /// <summary>
    /// Ensures the user is provisioned into all required apps.
    /// Apps the user is already provisioned into are skipped.
    /// Throws <see cref="ProvisioningException"/> if any app rejects or a callback fails.
    /// </summary>
    Task ProvisionAsync(AuthUser user, IReadOnlyList<string> requiredAppIds, CancellationToken ct = default);

    /// <summary>
    /// Provisions the user into all apps discovered from the registered <see cref="IProvisioningAppProvider"/>.
    /// </summary>
    Task ProvisionAsync(AuthUser user, CancellationToken ct = default);

    /// <summary>
    /// Deprovisions a user from all apps they are provisioned into.
    /// Best-effort: logs failures but does not throw.
    /// </summary>
    Task DeprovisionAllAsync(string userId, CancellationToken ct = default);
}

public sealed class ProvisioningException : Exception
{
    public string AppId { get; }
    public string? Reason { get; }

    public ProvisioningException(string appId, string? reason)
        : base($"Provisioning failed for app '{appId}': {reason ?? "unknown error"}")
    {
        AppId = appId;
        Reason = reason;
    }

    public ProvisioningException(string appId, string? reason, Exception inner)
        : base($"Provisioning failed for app '{appId}': {reason ?? "unknown error"}", inner)
    {
        AppId = appId;
        Reason = reason;
    }
}
