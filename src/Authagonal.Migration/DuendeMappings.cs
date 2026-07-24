using Authagonal.Core.Models;

namespace Authagonal.Migration;

/// <summary>
/// Pure, side-effect-free mapping helpers shared by the engine. Kept separate and internal so the
/// tricky bits — claim folding, client-secret tagging, id validation — are unit-testable without a
/// database or store.
/// </summary>
internal static class DuendeMappings
{
    // ASP.NET Identity / Duende emit both short OIDC claim types and the legacy xmlsoap URIs.
    private const string GivenNameXmlsoap = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname";
    private const string SurnameXmlsoap = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname";
    private const string EmailXmlsoap = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";

    /// <summary>
    /// Claim types that are folded onto first-class <see cref="AuthUser"/> fields or deliberately
    /// dropped (the email claims — email is already the user's key, storing it again as a custom
    /// attribute is noise). Everything NOT in this set falls through to <c>CustomAttributes</c>.
    /// </summary>
    private static readonly HashSet<string> FoldedOrExcludedTypes = new(StringComparer.Ordinal)
    {
        "given_name", GivenNameXmlsoap,
        "family_name", SurnameXmlsoap,
        "company",
        "org_id",
        "name",
        "email", EmailXmlsoap,
    };

    /// <summary>
    /// Folds Duende <c>AspNetUserClaims</c> onto <paramref name="user"/>:
    /// FirstName ⇐ given_name, LastName ⇐ family_name, CompanyName ⇐ company, OrganizationId ⇐ org_id
    /// (each also honoring the xmlsoap variant); a lone <c>name</c> is split when no explicit names
    /// exist; email claims are dropped; everything else becomes a custom attribute. When
    /// <paramref name="overwrite"/> is false an existing non-empty value / present attribute wins.
    /// </summary>
    public static void ApplyClaims(AuthUser user, IReadOnlyDictionary<string, string> claims, bool overwrite)
    {
        foreach (var (type, value) in claims)
        {
            switch (type)
            {
                case "given_name":
                case GivenNameXmlsoap:
                    if (overwrite || string.IsNullOrEmpty(user.FirstName)) user.FirstName = value;
                    break;
                case "family_name":
                case SurnameXmlsoap:
                    if (overwrite || string.IsNullOrEmpty(user.LastName)) user.LastName = value;
                    break;
                case "company":
                    if (overwrite || string.IsNullOrEmpty(user.CompanyName)) user.CompanyName = value;
                    break;
                case "org_id":
                    if (overwrite || string.IsNullOrEmpty(user.OrganizationId)) user.OrganizationId = value;
                    break;
                case "name":
                    if (string.IsNullOrEmpty(user.FirstName) && string.IsNullOrEmpty(user.LastName))
                    {
                        var parts = value.Split(' ', 2);
                        user.FirstName = parts[0];
                        if (parts.Length > 1) user.LastName = parts[1];
                    }
                    break;
                default:
                    // email claims and the folded types above never land in CustomAttributes.
                    if (FoldedOrExcludedTypes.Contains(type)) break;
                    if (overwrite || !user.CustomAttributes.ContainsKey(type)) user.CustomAttributes[type] = value;
                    break;
            }
        }
    }

    /// <summary>
    /// Tags a Duende client-secret hash by its digest length so the verifier knows which digest to
    /// recompute: a 44-char base64 body is a SHA-256 digest, 88-char is SHA-512. Any other length is
    /// an unrecognized secret format — returns null so the caller warns and drops it.
    /// </summary>
    public static string? TagClientSecret(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length switch
        {
            44 => "SHA256$" + trimmed,
            88 => "SHA512$" + trimmed,
            _ => null,
        };
    }

    // Table keys reject these characters (Azure Table PartitionKey/RowKey rules) and control chars.
    private static readonly char[] IllegalIdChars = ['/', '\\', '#', '?'];

    /// <summary>
    /// A Duende <c>sub</c> is preserved verbatim as the Authagonal user id, so it must be a legal
    /// Azure Table key: non-empty, ≤ 64 chars, free of <c>/ \ # ?</c> and control characters.
    /// </summary>
    public static bool IsValidUserId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return false;
        if (id.IndexOfAny(IllegalIdChars) >= 0)
            return false;
        foreach (var ch in id)
        {
            if (char.IsControl(ch))
                return false;
        }
        return true;
    }
}
