using System.Security.Claims;

namespace Authagonal.OidcProvider;

/// <summary>
/// The single extension point consumers must implement. Given a principal authenticated
/// by the host application (typically via cookie auth), return the subject payload used
/// to mint id_token / access_token claims and — when offline_access is granted — the
/// refresh token.
///
/// Return <c>null</c> to reject the request; the authorize endpoint will respond with
/// <c>login_required</c> so the client can retry interactive login.
/// </summary>
public interface IOidcSubjectResolver
{
    Task<OidcSubject?> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default);
}

public sealed record OidcSubjectResolutionContext(
    string ClientId,
    IReadOnlyList<string> RequestedScopes,
    IReadOnlyList<string> RequestedResources);

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
    /// Optional session cap. When set, refresh token lifetimes issued from this subject
    /// are clamped so the session cannot outlive this moment.
    /// </summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; init; }
}
