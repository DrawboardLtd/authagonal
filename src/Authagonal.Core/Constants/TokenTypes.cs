namespace Authagonal.Core.Constants;

/// <summary>
/// JWT <c>typ</c> header values, so a token's KIND is verifiable and not merely implied by which
/// endpoint returned it.
/// </summary>
/// <remarks>
/// Every JWT this server signs uses the same issuer and the same signing key, so signature validity
/// alone does not establish what a token IS. Before these values were stamped, every mint site emitted
/// the default <c>typ: JWT</c> — meaning an id_token or a back-channel logout token satisfied every check
/// the token-exchange endpoint applied to a <c>subject_token</c> and could be exchanged for a live access
/// token bearing the victim's <c>sub</c> and roles. Any path that accepts a token minted by this server
/// must pin the type it expects.
/// </remarks>
public static class TokenTypes
{
    /// <summary>RFC 9068 §2.1 — an OAuth 2.0 access token in JWT form.</summary>
    public const string AccessTokenJwt = "at+jwt";

    /// <summary>OIDC Back-Channel Logout 1.0 §2.4 — a logout token. Never an access token.</summary>
    public const string LogoutJwt = "logout+jwt";

    /// <summary>
    /// True when a validated token is an access token this server minted, rather than an id_token or a
    /// logout token that merely shares its issuer and signing key.
    /// </summary>
    /// <param name="typHeader">The token's <c>typ</c> header, if any.</param>
    /// <param name="hasClaim">Claim presence probe.</param>
    /// <remarks>
    /// Prefers the RFC 9068 <c>typ</c>, and falls back to claim shape so that access tokens minted before
    /// the header was stamped keep working through their remaining lifetime instead of being cut off at
    /// deploy. The fallback is sound because only an access token carries <c>client_id</c>: an id_token
    /// identifies its audience by <c>aud</c> and carries <c>nonce</c>, and a logout token carries
    /// <c>events</c>. Read paths use this; the token-exchange path pins <c>ValidTypes</c> outright and
    /// applies the claim checks on top, because that is where the confusion was exploitable.
    /// </remarks>
    public static bool IsAccessToken(string? typHeader, Func<string, bool> hasClaim)
    {
        if (string.Equals(typHeader, AccessTokenJwt, StringComparison.OrdinalIgnoreCase))
            return true;

        // Anything explicitly typed as something else is not an access token.
        if (!string.IsNullOrEmpty(typHeader)
            && !string.Equals(typHeader, "JWT", StringComparison.OrdinalIgnoreCase))
            return false;

        if (hasClaim("events") || hasClaim("nonce")) return false;
        return hasClaim("client_id");
    }
}
