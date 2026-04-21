using System.Security.Claims;

namespace Authagonal.OidcProvider;

/// <summary>
/// The single extension point consumers must implement. Given a principal authenticated
/// by the host application (typically via cookie auth), return either an
/// <see cref="OidcSubjectResult.Allowed"/> with the subject to mint, or an
/// <see cref="OidcSubjectResult.Rejected"/> carrying the OIDC error to surface back to
/// the client (for example, <see cref="OidcRejection.ConsentRequired"/> when the user
/// is authenticated but additional interaction is required).
/// </summary>
public interface IOidcSubjectResolver
{
    Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
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

public sealed class OidcSubject
{
    /// <summary>Stable identifier for the user. Emitted as the <c>sub</c> claim.</summary>
    public required string SubjectId { get; init; }

    public string? Email { get; init; }
    public bool EmailVerified { get; init; }
    public string? Name { get; init; }
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }

    /// <summary>Roles to emit as <c>role</c> claims on the access token.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Additional claims. Claim types here override the defaults above.</summary>
    public IReadOnlyDictionary<string, string>? AdditionalClaims { get; init; }

    /// <summary>
    /// Optional session cap. When set, access / id / refresh token lifetimes issued from
    /// this subject are clamped so no token — including those minted from rotations —
    /// outlives this moment. Typically sourced from an upstream IdP's <c>exp</c>-style
    /// claim so federation-anchored sessions can't be extended indefinitely.
    /// </summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; init; }
}
