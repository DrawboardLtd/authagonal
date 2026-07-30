namespace Authagonal.Core.Services;

/// <summary>
/// The single decision "is this value safe to put in a redirect target". Every open-redirect sink in the
/// product — SAML <c>RelayState</c>, the OIDC <c>returnUrl</c>, the BFF return URL, and the consent
/// screen — must route through here.
/// </summary>
/// <remarks>
/// There were four near-identical copies of this check and they had already drifted: two rejected any
/// embedded backslash, one only inspected the second character, and the TypeScript BFF package never
/// received the backslash fix at all. None of them rejected control characters, which is the bypass that
/// made all of them ineffective:
///
/// <para>
/// The WHATWG URL parser strips every ASCII tab, LF and CR from a URL <em>before</em> parsing it, and
/// Kestrel emits a tab verbatim in a <c>Location</c> header. So <c>"/" + "\t" + "/evil.example"</c> passes a
/// "starts with / and is not //" check, is sent unchanged, and is then parsed by the browser as
/// <c>//evil.example</c> — a scheme-relative URL pointing off-site. The redirect carries the identity
/// provider's own origin, which is exactly what makes it useful for phishing.
/// </para>
/// </remarks>
public static class LocalRedirect
{
    /// <summary>
    /// True when <paramref name="url"/> is a same-site relative path that no browser can read as an
    /// authority.
    /// </summary>
    public static bool IsSafeLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Control characters first: tab/LF/CR are removed by the URL parser, so any check that runs
        // before they are rejected is inspecting a different string from the one the browser will.
        foreach (var c in url)
            if (char.IsControl(c) || c == '')
                return false;

        // Must be rooted, and must not be readable as an authority. A backslash anywhere is refused
        // because WHATWG normalizes '\' to '/', so "/\evil.example" and "/x/..\\evil" both reach off-site.
        if (!url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\'))
            return false;

        return true;
    }

    /// <summary>
    /// <paramref name="url"/> when it is a safe local path, otherwise <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitize(string? url, string fallback = "/")
        => IsSafeLocalPath(url) ? url! : fallback;
}
