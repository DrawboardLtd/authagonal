namespace Authagonal.Core.Services;

/// <summary>
/// The administrative scope is reserved: no OAuth client may hold it. A client that did could mint
/// admin tokens indefinitely via <c>client_credentials</c>, which is privilege persistence that survives
/// the admin who created it losing their own access.
/// </summary>
/// <remarks>
/// Every path a CALLER can reach must consult this: the admin API, dynamic client registration, and
/// <c>POST /api/v1/token</c>.
/// <para>
/// Configuration seeding must NOT. It is the trust root rather than a caller — whoever writes the
/// <c>Clients:</c> section can already set <c>AdminApi:Scope</c>, replace the signing keys or repoint the
/// store — and a config-seeded <c>client_credentials</c> client holding this scope is the documented, sole
/// way to bootstrap the first admin token (<c>docs/admin-api.md</c>). Enforcing the reservation in the two
/// seeders locked fresh deployments out of their own admin API and turned admin-secret rotation into a
/// silent no-op; see <see cref="ClientSeedPolicy.Reject"/> for the full account.
/// </para>
/// </remarks>
public static class AdminScopeReservation
{
    /// <summary>Default admin scope name when <c>AdminApi:Scope</c> is not configured.</summary>
    public const string DefaultAdminScope = "authagonal-admin";

    /// <summary>
    /// True when <paramref name="requestedScopes"/> would grant <paramref name="adminScope"/>.
    /// </summary>
    /// <remarks>
    /// Each entry is split on whitespace before comparing. A stored scope entry is not necessarily a
    /// single scope token: <c>AllowedScopes</c> is joined into a space-delimited <c>scope</c> string on the
    /// wire, so an entry like <c>"authagonal-admin x"</c> is ONE opaque string to a whole-string
    /// comparison but TWO scopes to every consumer that splits — which made an embedded space a permanent
    /// admin backdoor. Comparison is case-insensitive because the authorization policy that consumes the
    /// minted scope claim matches that way; refusing the case variant here keeps the two consistent.
    /// </remarks>
    public static bool Grants(IEnumerable<string>? requestedScopes, string adminScope)
    {
        if (requestedScopes is null) return false;

        foreach (var entry in requestedScopes)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            foreach (var token in entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(token, adminScope, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    /// <summary>
    /// The first entry that is not a single valid scope token, or null when all are well-formed. A scope
    /// name containing whitespace is never legitimate (RFC 6749 §3.3 delimits scopes by space), and
    /// allowing one lets a single stored entry expand into several scopes downstream.
    /// </summary>
    public static string? FindMalformedScope(IEnumerable<string>? scopes)
    {
        if (scopes is null) return null;

        foreach (var s in scopes)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            foreach (var c in s)
                if (char.IsWhiteSpace(c)) return s;
        }

        return null;
    }
}
