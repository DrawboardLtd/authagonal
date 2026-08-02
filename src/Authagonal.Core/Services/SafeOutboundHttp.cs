using Microsoft.Extensions.Logging;

namespace Authagonal.Core.Services;

/// <summary>
/// Fetches a server-initiated outbound URL with <see cref="OutboundUrl"/> applied to EVERY hop.
/// </summary>
/// <remarks>
/// The validator was only ever consulted for the URL the caller supplied. The named
/// <see cref="HttpClient"/>s follow redirects automatically (the framework default,
/// <c>MaxAutomaticRedirections = 50</c>), so a 302 from an attacker-controlled host sent the request to a
/// target the guard never saw: point an IdP metadata URL at your own server, answer with
/// <c>Location: http://169.254.169.254/latest/meta-data/…</c>, and the response body comes back to the
/// parser. The review reproduced exactly that against a loopback listener.
///
/// <para>
/// Redirects are therefore resolved manually here. Two details matter. .NET refuses a secure→insecure
/// automatic redirect, so the practical entry point is an <c>http://</c> starting URL — which the validator
/// permits by design, since scheme policy is the caller's. And a response body is bounded, because these
/// endpoints are anonymous: an unbounded read is a memory amplifier regardless of where it points.
/// </para>
/// <para>
/// This lives in Core, not Server, because the client <c>jwks_uri</c> fetch that
/// <c>Authagonal.Protocol</c> performs during <c>private_key_jwt</c> authentication needs it too — that
/// call site is reachable from an anonymous <c>/connect/token</c> request and was doing a raw
/// <c>GetStringAsync</c> on a redirect-following client.
/// </para>
/// </remarks>
public static class SafeOutboundHttp
{
    /// <summary>Redirect hops allowed. Real metadata endpoints use none or one.</summary>
    private const int MaxHops = 5;

    /// <summary>Cap on a fetched document. Metadata and JWKS documents are kilobytes.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// GETs <paramref name="url"/> as a string, validating the initial URL and every redirect target.
    /// </summary>
    /// <param name="allowlist">
    /// Operator-configured internal destinations, applied to the initial URL and to every redirect target
    /// alike. Null — the default — on every registrant-supplied URL. See <see cref="OutboundAllowlist"/>.
    /// It must be the same allowlist the fetching client's <see cref="SafeOutboundConnect"/> callback was
    /// given, or one layer refuses what the other permits and the deployment fails in the layer nobody
    /// configured.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The URL, or a host it redirected to, failed the SSRF guard; or the response exceeded
    /// <see cref="MaxResponseBytes"/>.
    /// </exception>
    public static async Task<string> GetStringAsync(
        HttpClient client, string url, ILogger? logger = null, CancellationToken ct = default,
        OutboundAllowlist? allowlist = null)
    {
        var current = url;

        for (var hop = 0; hop <= MaxHops; hop++)
        {
            if (!OutboundUrl.IsSafe(current, allowlist: allowlist))
            {
                logger?.LogWarning("Refusing outbound fetch of {Url}: blocked by the SSRF guard", current);
                throw new InvalidOperationException("The requested URL is not permitted.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                // Resolve relative Locations against the URL we actually requested, then re-validate on the
                // next iteration. This is the check the automatic follower skipped.
                var next = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();

                // .NET refuses an https→http automatic redirect. Following hops by hand removed that
                // refusal along with the behaviour it was replacing — nothing here re-imposed it, so a
                // document fetched over https could hand back `Location: http://…` and the next hop was
                // made in cleartext, on a path whose whole reason for requiring https is that the response
                // is a trust anchor: SAML signing certificates, an OIDC issuer and jwks_uri. An on-path
                // party then substitutes the document and everything downstream validates against their
                // keys. Restored explicitly, and it is a refusal rather than an upgrade attempt because a
                // redirect to plaintext is not a thing a legitimate metadata endpoint does.
                if (IsSchemeDowngrade(current, next))
                {
                    logger?.LogWarning(
                        "Refusing outbound fetch of {Url}: it redirected from https to plaintext http",
                        current);
                    throw new InvalidOperationException("The requested URL is not permitted.");
                }

                current = next;
                response.Dispose();
                continue;
            }

            try
            {
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
                    throw new InvalidOperationException("The remote document is too large.");

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var limited = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (limited.Length + read > MaxResponseBytes)
                        throw new InvalidOperationException("The remote document is too large.");
                    limited.Write(buffer, 0, read);
                }

                return System.Text.Encoding.UTF8.GetString(limited.ToArray());
            }
            finally
            {
                response.Dispose();
            }
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    /// <summary>
    /// True when <paramref name="to"/> is plaintext http and <paramref name="from"/> was https.
    /// </summary>
    /// <remarks>
    /// Unparseable inputs answer false: they are not downgrades, and the caller's own
    /// <see cref="OutboundUrl.IsSafe"/> check on the next iteration is what refuses them. Two guards each
    /// answering only their own question beats one that half-answers both.
    /// </remarks>
    private static bool IsSchemeDowngrade(string from, string to)
        => Uri.TryCreate(from, UriKind.Absolute, out var previous)
            && Uri.TryCreate(to, UriKind.Absolute, out var next)
            && previous.Scheme == Uri.UriSchemeHttps
            && next.Scheme == Uri.UriSchemeHttp;
}
