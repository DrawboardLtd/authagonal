namespace Authagonal.Server.Services;

/// <summary>
/// Fetches a server-initiated outbound URL with <see cref="OutboundUrlValidator"/> applied to EVERY hop.
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
    /// <exception cref="InvalidOperationException">
    /// The URL, or a host it redirected to, failed the SSRF guard; or the response exceeded
    /// <see cref="MaxResponseBytes"/>.
    /// </exception>
    public static async Task<string> GetStringAsync(
        HttpClient client, string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var current = url;

        for (var hop = 0; hop <= MaxHops; hop++)
        {
            if (!OutboundUrlValidator.IsSafe(current))
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
                current = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();
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
}
