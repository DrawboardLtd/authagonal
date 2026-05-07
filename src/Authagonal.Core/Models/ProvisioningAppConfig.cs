namespace Authagonal.Core.Models;

/// <summary>
/// Persisted record of a downstream provisioning app. The orchestrator works in terms
/// of <see cref="Authagonal.Core.Services.ProvisioningApp"/> (a value record); this is
/// the storage shape the admin API exposes for CRUD.
/// </summary>
public sealed class ProvisioningAppConfig
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string? ApiKey { get; set; }

    /// <summary>
    /// Maximum seconds to wait for the /try callback. Null falls back to the
    /// orchestrator default (60s). Raise when the downstream app does real work
    /// during Try (e.g. a routing slip that spins up an organization).
    /// Confirm/Cancel/Deprovision use a short fixed timeout and are not tunable.
    /// </summary>
    public int? TryTimeoutSeconds { get; set; }
}
