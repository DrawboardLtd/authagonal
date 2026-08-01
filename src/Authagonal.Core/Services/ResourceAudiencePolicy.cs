using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// The one place that decides whether a client may name a given RFC 8707 <c>resource</c>.
/// </summary>
/// <remarks>
/// The rule was written out at three call sites — the authorize request, the client_credentials mint, and
/// the token exchange — and the three did not agree: exchange denied on an empty audience list while the
/// other two read empty as "unset, allow anything". The disagreement was deliberate and documented, but
/// restating a security rule three times is how the two permissive copies came to be the ones nobody
/// revisited. One function, called from all three.
/// <para>
/// Caps come with it. An <c>audiences</c> list arriving through dynamic client registration was stored
/// verbatim: unbounded length, unbounded entries, any string at all. That is the same unbounded-list
/// problem the redirect-URI caps close, on a field that ends up in a signed token's <c>aud</c>.
/// </para>
/// </remarks>
public static class ResourceAudiencePolicy
{
    /// <summary>Most audiences one client may declare.</summary>
    public const int MaxAudiences = 20;

    /// <summary>Longest single audience value.</summary>
    public const int MaxAudienceLength = 512;

    /// <summary>
    /// Why a <c>resource</c> value is unacceptable for <paramref name="client"/>, or null when it is fine.
    /// </summary>
    /// <remarks>
    /// Three outcomes, and the middle one is the point of <see cref="OAuthClient.AudiencesDeclared"/>:
    /// <list type="bullet">
    /// <item>The client declared audiences — the resource must be one of them.</item>
    /// <item>The client declared none, deliberately — no resource may be named.</item>
    /// <item>The client predates the flag — anything absolute is accepted, as before, because tightening
    /// stored rows on upgrade would break flows that work today.</item>
    /// </list>
    /// </remarks>
    public static string? RejectResource(OAuthClient client, string resource)
    {
        if (!IsAbsoluteUriWithWrittenScheme(resource))
            return $"resource '{resource}' is not a valid absolute URI";

        if (client.Audiences.Count > 0)
        {
            return client.Audiences.Contains(resource, StringComparer.Ordinal)
                ? null
                : $"resource '{resource}' is not registered for this client";
        }

        // Declared, and declared nothing. Naming a resource is not access to it — the value only narrows
        // `aud` — but a token this issuer signed, carrying a user's `sub` and an `aud` the client chose, is
        // handed to a resource server that has to be relied on to check `scope` rather than to read a
        // matching iss + aud + sub as permission. A client that was asked and answered "none" does not get
        // to name one.
        if (client.AudiencesDeclared)
            return "this client declares no audiences, so it may not request a resource";

        return null;
    }

    /// <summary>
    /// Why a declared <c>audiences</c> list is unacceptable, or null when it is fine. Applied where a
    /// client is written, so an unbounded or malformed list never reaches the store.
    /// </summary>
    public static string? RejectAudiences(IReadOnlyList<string> audiences)
    {
        if (audiences.Count > MaxAudiences)
            return $"audiences accepts at most {MaxAudiences} entries";

        foreach (var audience in audiences)
        {
            if (string.IsNullOrWhiteSpace(audience))
                return "audiences must not contain an empty entry";

            if (audience.Length > MaxAudienceLength)
                return $"each audience must be at most {MaxAudienceLength} characters";

            // An audience becomes the `aud` of a signed token and is compared verbatim against a
            // resource-server's expected value, so it has to be the same kind of thing a `resource`
            // parameter is: an absolute URI with no fragment.
            if (!IsAbsoluteUriWithWrittenScheme(audience))
                return $"audience '{audience}' is not a valid absolute URI";
        }

        return null;
    }

    /// <summary>
    /// An absolute URI whose scheme is actually written, with no fragment.
    /// </summary>
    /// <remarks>
    /// <c>Uri.TryCreate(value, UriKind.Absolute, …)</c> is not sufficient on its own, and this is a
    /// platform difference rather than a subtlety: on Unix — which is what the server runs on — it parses
    /// <c>/admin</c> and <c>//host/path</c> as ABSOLUTE URIs by INFERRING a <c>file:</c> scheme. So the
    /// plain check accepted a bare path as a resource indicator, and the value then landed verbatim in a
    /// signed token's <c>aud</c>. Requiring the value to begin with the scheme the parser reported is what
    /// separates a URI that says what it is from a path the parser guessed at.
    /// </remarks>
    private static bool IsAbsoluteUriWithWrittenScheme(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && string.IsNullOrEmpty(uri.Fragment)
           && value.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase);
}
