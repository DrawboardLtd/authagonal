using System.Security.Claims;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Server.Services;

/// <summary>
/// Drops the upstream IdP refresh token belonging to a federated session that is ending.
/// </summary>
/// <remarks>
/// When a connection sets <c>RevalidateOnRefresh</c>, the OIDC callback persists the upstream refresh
/// token into <see cref="IUpstreamRefreshTokenStore"/> keyed by (userId, connectionId, sid), with a
/// flat seven-day expiry. Every reference to that store had exactly one <c>RemoveAsync</c> call site —
/// the resolver, when the upstream itself answers <c>invalid_grant</c>. No logout path, no session
/// revoke, no back-channel logout touched it, so the row outlived the session it belongs to by up to
/// a week.
/// <para>
/// It is a live bearer credential for ANOTHER identity provider. "Sign out" and "revoke this session"
/// both mean the session's credentials stop existing, and this one did not.
/// </para>
/// <para>
/// This covers a host with NO server-side ticket store, where a sign-out is the only moment the
/// principal is in hand. A host that does use one gets the same cleanup from
/// <c>TableTicketStore.RemoveAsync</c>, which recovers the principal from the stored ticket and so
/// catches every other way a session ends too — revoking one from the account page, "sign out
/// everywhere", the expiry sweep. Both paths are idempotent, so a session covered by both is fine.
/// </para>
/// </remarks>
internal static class UpstreamSessionCleanup
{
    /// <summary>
    /// Removes the row for the session the caller is currently signed in as. Best effort — a sign-out
    /// must complete whether or not the store is reachable.
    /// </summary>
    public static async Task RemoveForPrincipalAsync(HttpContext httpContext, CancellationToken ct)
    {
        var store = httpContext.RequestServices.GetService<IUpstreamRefreshTokenStore>();
        if (store is null) return;

        var principal = httpContext.User;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var connectionId = principal.FindFirstValue("upstream_connection_id");
        var sid = principal.FindFirstValue("sid");

        // All three are part of the key. A session that carries no upstream connection was not
        // federated with revalidation on, so there is nothing to remove.
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(sid))
            return;

        try
        {
            await store.RemoveAsync(userId, connectionId, sid, ct);
        }
        catch (Exception ex)
        {
            httpContext.RequestServices
                .GetService<ILoggerFactory>()?
                .CreateLogger(typeof(UpstreamSessionCleanup))
                .LogWarning(ex,
                    "Could not remove the upstream refresh token for session {Sid}; it will expire on its own",
                    sid);
        }
    }
}
