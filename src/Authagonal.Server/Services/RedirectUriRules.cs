namespace Authagonal.Server.Services;

/// <summary>
/// What a registered redirection endpoint may look like, in one place.
/// </summary>
/// <remarks>
/// These rules lived only in the dynamic-registration endpoint, so the two registration paths
/// disagreed about what a valid redirect URI is — and the PRIVILEGED one was the permissive one. The
/// admin client API bound a raw OAuthClient from the request body and wrote RedirectUris and
/// PostLogoutRedirectUris to the store untouched, while anonymous DCR required an absolute URI, no
/// fragment, https outside loopback, and no script pseudo-scheme.
/// </remarks>
public static class RedirectUriRules
{
    /// <summary>Schemes that are not network locations at all — registering one aims the
    /// authorization response at code the browser executes rather than at a client.</summary>
    private static readonly string[] ExecutableSchemes = ["javascript", "data", "vbscript", "file"];

    /// <summary>
    /// Returns null when every URI is acceptable, or a message naming the first that is not.
    /// </summary>
    /// <param name="requireHttps">
    /// Refuse cleartext http outside loopback. True for redirect URIs, where an authorization code
    /// travels; RFC 8252 §7.3 requires the loopback exemption for native apps. Post-logout URIs carry
    /// no credential, so they are checked for shape only — an admin registering an http intranet
    /// landing page there is not shipping anything an on-path party could use.
    /// </param>
    public static string? Validate(IEnumerable<string>? uris, string parameterName, bool requireHttps)
    {
        if (uris is null) return null;

        foreach (var uri in uris)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return $"{parameterName} must not contain an empty entry";

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return $"{parameterName} must be an absolute URI: {uri}";

            if (ExecutableSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
                return $"{parameterName} must not use the '{parsed.Scheme}' scheme: {uri}";

            // RFC 6749 §3.1.2 requires the redirection endpoint to carry no fragment, and the
            // fragment is where an implicit-style response would put a token — so a registered URI
            // with one is either a mistake or an attempt to shape where credentials land.
            if (!string.IsNullOrEmpty(parsed.Fragment))
                return $"{parameterName} must not contain a fragment: {uri}";

            if (requireHttps && parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
                return $"{parameterName} must use https (http is permitted only for loopback): {uri}";
        }

        return null;
    }
}
