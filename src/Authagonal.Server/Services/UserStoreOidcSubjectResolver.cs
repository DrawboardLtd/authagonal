using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol;

namespace Authagonal.Server.Services;

/// <summary>
/// Server's <see cref="IOidcSubjectResolver"/>: maps an authenticated
/// <see cref="ClaimsPrincipal"/> to an <see cref="OidcSubject"/> by looking up the
/// corresponding <see cref="AuthUser"/> in the user store and inflating groups from
/// the SCIM group store. On refresh, re-reads the user to pick up deactivation,
/// role changes, and fresh group membership — the token endpoint then mints against
/// this fresh subject, so nothing survives deactivation across a refresh.
/// </summary>
public sealed class UserStoreOidcSubjectResolver(
    IUserStore userStore,
    IScimGroupStore scimGroupStore,
    IClientStore clientStore) : IOidcSubjectResolver
{
    public async Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var subjectId = authenticatedPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authenticatedPrincipal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subjectId))
            return OidcSubjectResult.Reject(OidcRejection.LoginRequired, "No subject claim on principal");

        var user = await userStore.GetAsync(subjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Propagate the upstream-federation cap captured by the cookie (session_max_exp).
        // This is set at sign-in time when an IdP asserts a session lifetime.
        DateTimeOffset? sessionMaxExpiresAt = null;
        var sessionMaxExpClaim = authenticatedPrincipal.FindFirstValue("session_max_exp");
        if (!string.IsNullOrEmpty(sessionMaxExpClaim) &&
            long.TryParse(sessionMaxExpClaim, out var sessionMaxExpSeconds))
        {
            sessionMaxExpiresAt = DateTimeOffset.FromUnixTimeSeconds(sessionMaxExpSeconds);
        }

        var sessionId = authenticatedPrincipal.FindFirstValue("sid");

        var subject = await BuildSubjectAsync(user, client, sessionMaxExpiresAt, sessionId, ct);
        return OidcSubjectResult.Allow(subject);
    }

    public async Task<OidcSubjectResult> ResolveRefreshAsync(
        OidcSubject priorSubject,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var user = await userStore.GetAsync(priorSubject.SubjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Preserve the federation cap and session id across rotations — the resolver can't
        // re-read either from the cookie at refresh time, and both must survive rotations
        // so the cap can't be lifted and back-channel logouts can still correlate.
        var subject = await BuildSubjectAsync(user, client, priorSubject.SessionMaxExpiresAt, priorSubject.SessionId, ct);
        return OidcSubjectResult.Allow(subject);
    }

    /// <summary>
    /// Builds an <see cref="OidcSubject"/> from an <see cref="AuthUser"/>. Exposed for
    /// device-code and admin token paths that already know the subject and don't go
    /// through the authorize endpoint.
    /// </summary>
    public async Task<OidcSubject> BuildSubjectAsync(
        AuthUser user,
        OAuthClient? client,
        DateTimeOffset? sessionMaxExpiresAt = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<string>? groups = null;
        if (client is { IncludeGroupsInTokens: true })
        {
            var scimGroups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);
            if (scimGroups.Count > 0)
                groups = scimGroups.Select(g => g.DisplayName).ToList();
        }

        return new OidcSubject
        {
            SubjectId = user.Id,
            Email = user.Email,
            EmailVerified = user.EmailConfirmed,
            GivenName = user.FirstName,
            FamilyName = user.LastName,
            Phone = user.Phone,
            OrganizationId = user.OrganizationId,
            Roles = user.Roles.Count > 0 ? user.Roles.ToList() : null,
            Groups = groups,
            CustomAttributes = user.CustomAttributes.Count > 0
                ? user.CustomAttributes.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            SessionMaxExpiresAt = sessionMaxExpiresAt,
            SessionId = sessionId,
        };
    }
}
