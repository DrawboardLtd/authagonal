using System.Security.Claims;

namespace Authagonal.Core.Authority;

/// <summary>
/// Resource-side helper: answers "may this validated token do <c>action</c> on connector
/// <c>type</c> with these parameters" from the token's <c>authorization_details</c> claim.
/// Used by host resource servers and by the BFF proxy's per-route authority checks. A token
/// with no authority claim evaluates as <see cref="AuthoritySet.Unrestricted"/> — coarse
/// scope-based tokens keep working; the claim only ever narrows.
/// </summary>
public static class AuthorityEvaluator
{
    /// <summary>Extract the authority carried by a validated principal.</summary>
    public static AuthoritySet FromPrincipal(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst(AuthorityClaims.AuthorizationDetails);
        return claim is null ? AuthoritySet.Unrestricted : ParseOrDeny(claim.Value);
    }

    /// <summary>Extract the authority from a raw claim value (e.g. out of an introspection
    /// response or a hand-validated JWT payload). Null/empty = no claim = unrestricted.</summary>
    public static AuthoritySet FromClaimValue(string? authorizationDetailsJson) =>
        string.IsNullOrEmpty(authorizationDetailsJson)
            ? AuthoritySet.Unrestricted
            : ParseOrDeny(authorizationDetailsJson);

    /// <summary>Convenience for the common one-shot check. See
    /// <see cref="AuthoritySet.Permits"/> for context semantics.</summary>
    public static bool Permits(
        ClaimsPrincipal principal, string type, string action,
        IReadOnlyDictionary<string, string>? context = null) =>
        FromPrincipal(principal).Permits(type, action, context);

    // A present-but-unparseable claim is a deny, not an unrestricted fallback: a garbled
    // narrow token must never evaluate wider than it was minted.
    private static AuthoritySet ParseOrDeny(string json) =>
        AuthorityJson.TryParse(json, out var set) ? set : AuthoritySet.Empty;
}
