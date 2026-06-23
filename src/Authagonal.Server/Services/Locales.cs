namespace Authagonal.Server.Services;

/// <summary>
/// Shared normalization for an inbound user locale tag, used everywhere a locale enters the system:
/// self-service registration / reset / profile, and SCIM provisioning. We store the raw BCP-47 tag
/// (e.g. "de", "fr-CA") rather than folding it to a supported set — consumers resolve it to a concrete
/// language at use time (email rendering strips the region, the OIDC <c>locale</c> claim carries it
/// verbatim, the UI matches as best it can). This only trims and rejects empty / implausibly long
/// values so a junk tag can't be persisted.
/// </summary>
public static class Locales
{
    public static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var trimmed = locale.Trim();
        return trimmed.Length <= 35 ? trimmed : null;
    }
}
