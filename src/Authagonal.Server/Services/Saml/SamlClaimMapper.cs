namespace Authagonal.Server.Services.Saml;

public sealed record SamlUserInfo(
    string? Email,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    string? ObjectId,
    string NameId)
{
    /// <summary>Group/role memberships asserted by the IdP (multi-valued attribute), if any.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];
}

public static class SamlClaimMapper
{
    // Microsoft claim-URI names (Entra ID / ADFS default claim set)
    private const string ClaimEmailAddress = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
    private const string ClaimName = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
    private const string ClaimGivenName = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname";
    private const string ClaimSurname = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname";
    private const string ClaimObjectId = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string ClaimDisplayName = "http://schemas.microsoft.com/identity/claims/displayname";
    private const string ClaimGroups = "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups";
    private const string ClaimRole = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    // F50: per-field alias lists tried in order. First alias = the historic Microsoft claim URI so
    // Entra/ADFS behaviour is unchanged; the rest cover the friendly/OID names Okta, OneLogin, Ping,
    // Google and Shibboleth emit by default. Attribute dictionaries are case-insensitive and indexed
    // by both Name and FriendlyName, so one table covers both spellings.
    private static readonly string[] EmailAliases =
        [ClaimEmailAddress, "email", "mail", "emailaddress", "urn:oid:0.9.2342.19200300.100.1.3"];
    private static readonly string[] FirstNameAliases =
        [ClaimGivenName, "givenName", "given_name", "firstName", "first_name", "urn:oid:2.5.4.42"];
    private static readonly string[] LastNameAliases =
        [ClaimSurname, "sn", "surname", "lastName", "last_name", "familyName", "family_name", "urn:oid:2.5.4.4"];
    private static readonly string[] DisplayNameAliases =
        [ClaimDisplayName, "displayName", "urn:oid:2.16.840.1.113730.3.1.241", "cn", "urn:oid:2.5.4.3"];
    private static readonly string[] ObjectIdAliases =
        [ClaimObjectId, "objectGUID", "user.objectid"];
    private static readonly string[] GroupsAliases =
        [ClaimGroups, "groups", "memberOf", ClaimRole, "urn:oid:1.3.6.1.4.1.5923.1.5.1.1"];

    public static SamlUserInfo MapClaims(string nameId, string? nameIdFormat, Dictionary<string, string> attributes)
        => MapClaims(nameId, nameIdFormat, attributes, multiValues: null);

    public static SamlUserInfo MapClaims(
        string nameId,
        string? nameIdFormat,
        Dictionary<string, string> attributes,
        Dictionary<string, List<string>>? multiValues)
    {
        var email = First(attributes, EmailAliases);
        var name = attributes.GetValueOrDefault(ClaimName);
        var firstName = First(attributes, FirstNameAliases);
        var lastName = First(attributes, LastNameAliases);
        var displayName = First(attributes, DisplayNameAliases);
        var objectId = First(attributes, ObjectIdAliases);

        // Email resolution priority:
        // 1. Explicit email attribute (any alias)
        // 2. If NameID format is emailAddress, use NameID
        // 3. If the name claim looks like an email (contains @), use it
        // 4. Null (caller must handle)
        if (string.IsNullOrWhiteSpace(email))
        {
            if (string.Equals(nameIdFormat, SamlConstants.NameIdEmail, StringComparison.OrdinalIgnoreCase))
                email = nameId;
            else if (!string.IsNullOrWhiteSpace(name) && name.Contains('@'))
                email = name;
        }

        // Groups come from the multi-valued view when available (one AttributeValue per group); the
        // single-value dictionary would silently truncate to the first membership.
        List<string> groups = [];
        if (multiValues is not null)
        {
            foreach (var alias in GroupsAliases)
            {
                if (multiValues.TryGetValue(alias, out var values) && values.Count > 0)
                {
                    groups = values;
                    break;
                }
            }
        }

        return new SamlUserInfo(email, firstName, lastName, displayName, objectId, nameId) { Groups = groups };
    }

    private static string? First(Dictionary<string, string> attributes, string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (attributes.TryGetValue(alias, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }
}
