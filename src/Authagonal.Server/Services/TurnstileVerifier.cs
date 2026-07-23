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
/// Verifies Cloudflare Turnstile tokens against the siteverify endpoint. Fails closed when
/// Turnstile is configured; a no-op pass when it isn't (opt-in).
/// </summary>
public sealed class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger)
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Whether Turnstile is configured (a secret key is present).</summary>
    public bool Enabled => options.Value.Enabled;

    /// <summary>
    /// Returns true if the token validates — or if Turnstile isn't configured (opt-out, no enforcement).
    /// Returns false when Turnstile is configured but the token is missing or rejected by Cloudflare.
    /// </summary>
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var secret = options.Value.SecretKey;
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
