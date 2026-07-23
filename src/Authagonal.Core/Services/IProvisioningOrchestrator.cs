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
    /// Re-runs the Try/Confirm cycle for every discovered app even if the user is already provisioned
    /// into it. Use when the user's relationship to the downstream materially CHANGES and the app must
    /// react again — e.g. a passwordless-account claim (guest → standard-user upgrade), where the
    /// original provisioning was a lightweight adoption and the claim now carries real signup context
    /// (organization name, etc.). A plain re-login must NOT use this (that's <see cref="ProvisionAsync(AuthUser, CancellationToken)"/>,
    /// which skips already-provisioned apps). Throws <see cref="ProvisioningException"/> if any app rejects.
    /// </summary>
    /// <remarks>
    /// Defaults to a no-op so adding this method doesn't source-break external implementors — a consumer
    /// that predates it keeps compiling and simply won't reprovision on upgrade until it overrides this.
    /// The shipped <c>TccProvisioningOrchestrator</c> overrides it.
    /// </remarks>
    Task ReprovisionAsync(AuthUser user, CancellationToken ct = default) => Task.CompletedTask;

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
