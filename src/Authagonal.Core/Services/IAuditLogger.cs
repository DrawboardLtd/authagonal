namespace Authagonal.Core.Services;

/// <summary>
/// Records audit trail entries for configuration changes and security-relevant events.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(string actor, string action, string entityType, string? entityId = null, string? detail = null, CancellationToken ct = default);
}

/// <summary>
/// No-op audit logger for environments where auditing is not configured.
/// </summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public Task LogAsync(string actor, string action, string entityType, string? entityId = null, string? detail = null, CancellationToken ct = default)
        => Task.CompletedTask;
}
