using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

/// <summary>
/// Cloudflare Turnstile ("I'm human") configuration, bound from the "Turnstile" config section.
/// </summary>
/// <remarks>
/// Opt-in: Turnstile is enforced only when <see cref="SecretKey"/> is set. Consumers that don't
/// configure it (the demo server, an embedded guest-OIDC host, etc.) keep the unchanged login/register
/// flow — no widget rendered, no token required.
/// </remarks>
public sealed class TurnstileOptions
{
    /// <summary>Public site key, surfaced to the login UI so it can render the widget. Null when disabled.</summary>
    public string? SiteKey { get; set; }

    /// <summary>Secret key for server-side token verification. Null/empty disables Turnstile entirely.</summary>
    public string? SecretKey { get; set; }

    /// <summary>True when a secret key is configured, i.e. Turnstile verification is enforced.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Supplies the Turnstile key pair for the CURRENT request.
/// </summary>
/// <remarks>
/// A sitekey is not a shared secret — it names a widget record at Cloudflare carrying an allowlist of
/// hostnames, and the widget issues no token on any host outside it (client-side error 110200, so the
/// form's submit never enables and nothing reaches siteverify to explain why). A host serving ONE domain
/// has one widget and reads both keys from <see cref="TurnstileOptions"/>, which is what
/// <see cref="OptionsTurnstileKeyProvider"/> does and what every existing consumer keeps.
///
/// A host serving customer-supplied domains cannot: Cloudflare caps a widget at 10 hostnames (200 on
/// Enterprise), so past that the domains must be spread over several widgets and the key pair depends on
/// which widget the requesting host was allocated. Such a host registers its own scoped implementation.
///
/// Both keys come from ONE object deliberately. Resolving them independently is the mistake this seam
/// exists to prevent: the browser would render widget A while the server verified against widget B, and
/// Cloudflare reports that mismatch as an ordinary failed verification — every login silently rejected,
/// with nothing in the response to say why.
/// </remarks>
public interface ITurnstileKeyProvider
{
    /// <summary>Public sitekey for the widget this request should render. Null when disabled.</summary>
    string? SiteKey { get; }

    /// <summary>Secret paired with <see cref="SiteKey"/>. Null/empty disables enforcement.</summary>
    string? SecretKey { get; }
}

/// <summary>The single-widget default: both keys come from configuration.</summary>
public sealed class OptionsTurnstileKeyProvider(IOptions<TurnstileOptions> options) : ITurnstileKeyProvider
{
    public string? SiteKey => options.Value.SiteKey;
    public string? SecretKey => options.Value.SecretKey;
}

/// <summary>
/// Verifies Cloudflare Turnstile tokens against the siteverify endpoint. Fails closed when
/// Turnstile is configured; a no-op pass when it isn't (opt-in).
/// </summary>
public sealed class TurnstileVerifier(
    HttpClient httpClient,
    ITurnstileKeyProvider keys,
    ILogger<TurnstileVerifier> logger)
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Whether Turnstile is configured (a secret key is present).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(keys.SecretKey);

    /// <summary>
    /// Returns true if the token validates — or if Turnstile isn't configured (opt-out, no enforcement).
    /// Returns false when Turnstile is configured but the token is missing or rejected by Cloudflare.
    /// </summary>
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var secret = keys.SecretKey;
        if (string.IsNullOrWhiteSpace(secret))
            return true; // not configured -> don't enforce

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var form = new List<KeyValuePair<string, string>>
        {
            new("secret", secret),
            new("response", token),
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
            form.Add(new("remoteip", remoteIp));

        try
        {
            using var resp = await httpClient.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // Parse with JsonDocument (trim/AOT-safe — no reflection-based deserialization).
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            // Fail closed: when Turnstile is enforced and verification can't complete, reject.
            logger.LogWarning(ex, "Turnstile siteverify request failed");
            return false;
        }
    }
}
