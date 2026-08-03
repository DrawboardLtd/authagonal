using Authagonal.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Says at startup when the admin audit trail is going nowhere.
/// </summary>
/// <remarks>
/// Every admin write calls <c>audit.LogAsync(...)</c>, and a convention test keeps it that way. But the default
/// registration binds <c>IAuditLogger</c> to <see cref="NullAuditLogger"/>, whose <c>LogAsync</c> returns a
/// completed task — and no implementation is bundled, nor supplied by any provider package. So on a self-hosted
/// install none of those rows exist: MFA reset and credential removal, <c>POST /api/v1/token</c> (which mints a
/// token AS another user), set-password, delete, confirm-email, identity linking, SCIM token creation, SSO
/// connection create and repoint, role and scope edits.
/// <para>
/// The comments at those call sites assert the opposite as accomplished fact — "audited, not merely logged … an
/// incident responder asking who reset this account's MFA and when had nowhere to look". With the null sink
/// they still have nowhere to look, and now nothing says so. A trail that silently goes nowhere is worse than a
/// missing one, because it is relied upon.
/// </para>
/// <para>
/// A warning rather than a refusal to start: an evaluation or single-operator deployment is a legitimate place
/// to run without an audit sink, and failing startup over it would be a breaking change for every existing
/// install. The point is that the operator finds out at boot rather than during an incident.
/// </para>
/// </remarks>
internal sealed class NullAuditLoggerWarning(
    IAuditLogger auditLogger,
    ILogger<NullAuditLoggerWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (auditLogger is not NullAuditLogger) return Task.CompletedTask;

        logger.LogWarning(
            "No IAuditLogger is registered, so the administrative audit trail is discarded. Every admin write "
            + "still calls it — MFA reset and credential removal, minting a token as another user, "
            + "set-password, account deletion, identity linking, SCIM token creation, SSO connection changes, "
            + "role and scope edits — and none of it is recorded anywhere queryable by subject. Register an "
            + "IAuditLogger implementation to keep those rows; until then an incident cannot reconstruct who "
            + "changed what on whose account.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
