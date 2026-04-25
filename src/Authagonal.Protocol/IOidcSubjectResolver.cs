using System.Security.Claims;

namespace Authagonal.Protocol;

/// <summary>
/// The single extension point hosts must implement. Describes how to turn a request
/// at <c>/connect/authorize</c> (or a refresh at <c>/connect/token</c>) into the
/// <see cref="OidcSubject"/> that Authagonal.Protocol will mint tokens for.
/// <para>
/// At authorize, <see cref="ResolveAsync"/> is called with whatever principal the
/// host's authentication scheme produced (cookie login, custom share-link handler,
/// etc.). At refresh, <see cref="ResolveRefreshAsync"/> is called with the subject
/// captured at the original authorize call, so the host can re-validate the session
/// — deactivation, revoked share links, changed roles — without extending the
/// refresh chain indefinitely.
/// </para>
/// </summary>
public interface IOidcSubjectResolver
{
    /// <summary>
    /// Resolve the subject at the authorize endpoint. The principal has already been
    /// authenticated by the host's scheme.
    /// </summary>
    Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Re-resolve the subject when a refresh token is redeemed. Hosts should re-check
    /// that the identity is still valid (user not deactivated, share link not revoked,
    /// etc.) and return a fresh <see cref="OidcSubject"/>. Returning
    /// <see cref="OidcRejection.AccessDenied"/> revokes the refresh chain.
    /// </summary>
    Task<OidcSubjectResult> ResolveRefreshAsync(
        OidcSubject priorSubject,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default);
}

public sealed record OidcSubjectResolutionContext(
    string ClientId,
    IReadOnlyList<string> RequestedScopes,
    IReadOnlyList<string> RequestedResources);

public abstract record OidcSubjectResult
{
    private OidcSubjectResult() { }

    public static OidcSubjectResult Allow(OidcSubject subject) => new Allowed(subject);

    public static OidcSubjectResult Reject(OidcRejection reason, string? description = null) =>
        new Rejected(reason, description);

    public sealed record Allowed(OidcSubject Subject) : OidcSubjectResult;

    public sealed record Rejected(OidcRejection Reason, string? Description) : OidcSubjectResult;
}

/// <summary>
/// OIDC error codes the subject resolver may surface. Maps to the standard
/// <c>error</c> value in the authorize response.
/// </summary>
public enum OidcRejection
{
    /// <summary>User must re-authenticate. Maps to <c>login_required</c>.</summary>
    LoginRequired,

    /// <summary>User has not consented to the requested scopes. Maps to <c>consent_required</c>.</summary>
    ConsentRequired,

    /// <summary>Multiple accounts are available and the user must pick one. Maps to <c>account_selection_required</c>.</summary>
    AccountSelectionRequired,

    /// <summary>Authenticated user is not permitted for this request. Maps to <c>access_denied</c>.</summary>
    AccessDenied,
}

/// <summary>
/// The resolved identity Authagonal.Protocol mints tokens for. Hosts build this from
/// their own identity model (AuthUser, share-link claim set, whatever) and return it
/// from <see cref="IOidcSubjectResolver"/>.
/// </summary>
public sealed class OidcSubject
{
    /// <summary>Stable identifier for the user. Emitted as the <c>sub</c> claim.</summary>
    public required string SubjectId { get; init; }

    public string? Email { get; init; }
    public bool EmailVerified { get; init; }
    public string? Name { get; init; }
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }
    public string? Phone { get; init; }

    /// <summary>
    /// Convenience slot for the <c>org_id</c> claim used across Authagonal's product
    /// line. Hosts that don't use it can leave it null — nothing else reads it.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>Roles to emit as <c>roles</c> claims on access and id tokens.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Group display names to emit as <c>groups</c> claims. Typically sourced from SCIM.</summary>
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>
    /// Custom attributes carried by the subject. Each entry is emitted as a claim only
    /// if a requested scope's <c>UserClaims</c> whitelist releases that claim name.
    /// Reserved OAuth/OIDC protocol claim names (<c>iss</c>, <c>sub</c>, <c>aud</c>,
    /// <c>exp</c>, <c>iat</c>, <c>scope</c>, <c>client_id</c>, <c>roles</c>, <c>groups</c>,
    /// etc.) cannot be shadowed even if a scope lists them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomAttributes { get; init; }

    /// <summary>
    /// Per-session claims sourced from an upstream OIDC IdP during federation. Same
    /// scope-gated emission rules as <see cref="CustomAttributes"/>, but distinct so
    /// they survive across refresh rotations without bleeding into the per-user record.
    /// On refresh, the host carries this set forward unchanged from the prior subject;
    /// <see cref="CustomAttributes"/> is re-read fresh from the user store. Federation
    /// values win on key collision.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FederationClaims { get; init; }

    /// <summary>
    /// Additional claims to force onto the access token regardless of scope gating.
    /// Used by hosts with a short-lived, bounded-scope use case (e.g. share-link
    /// tokens carrying <c>link_share_token</c>) where the claim is the whole point of
    /// the token. Reserved protocol claim names are still blocked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalClaims { get; init; }

    /// <summary>
    /// Optional session cap. When set, access / id / refresh token lifetimes issued from
    /// this subject are clamped so no token — including those minted from rotations —
    /// outlives this moment. Typically sourced from an upstream IdP's <c>exp</c>-style
    /// claim, or from the absolute expiry of a share link.
    /// </summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; init; }

    /// <summary>
    /// Stable per-authentication-session identifier. Emitted as the <c>sid</c> claim on ID
    /// tokens and propagated into back-channel logout tokens when the relying party has
    /// <c>BackChannelLogoutSessionRequired</c> set. Generated at sign-in by the host and
    /// preserved across refresh rotations so RPs can correlate logout events back to the
    /// original login.
    /// </summary>
    public string? SessionId { get; init; }
}
