namespace Authagonal.Server.Services.Saml;

public sealed record SamlUserInfo(
    string? Email,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    string? ObjectId,
    string NameId);

public static class SamlClaimMapper
{
    private const string ClaimEmailAddress = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
    private const string ClaimName = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
    private const string ClaimGivenName = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname";
    private const string ClaimSurname = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname";
    private const string ClaimObjectId = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string ClaimDisplayName = "http://schemas.microsoft.com/identity/claims/displayname";

    public static SamlUserInfo MapClaims(string nameId, string? nameIdFormat, Dictionary<string, string> attributes)
    {
        attributes.TryGetValue(ClaimEmailAddress, out var email);
        attributes.TryGetValue(ClaimName, out var name);
        attributes.TryGetValue(ClaimGivenName, out var firstName);
        attributes.TryGetValue(ClaimSurname, out var lastName);
        attributes.TryGetValue(ClaimObjectId, out var objectId);
        attributes.TryGetValue(ClaimDisplayName, out var displayName);

        // Email resolution priority:
        // 1. Explicit emailaddress claim
        // 2. If NameID format is emailAddress, use NameID
        // 3. If name claim looks like an email (contains @), use it
        // 4. Null (caller must handle)
        if (string.IsNullOrWhiteSpace(email))
        {
            if (string.Equals(nameIdFormat, SamlConstants.NameIdEmail, StringComparison.OrdinalIgnoreCase))
                email = nameId;
            else if (!string.IsNullOrWhiteSpace(name) && name.Contains('@'))
                email = name;
        }

        return new SamlUserInfo(email, firstName, lastName, displayName, objectId, nameId);
    }
}
