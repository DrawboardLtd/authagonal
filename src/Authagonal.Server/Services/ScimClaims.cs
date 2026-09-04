namespace Authagonal.Server.Services;

/// <summary>
/// Claim types on the principal a SCIM bearer token authenticates as.
/// </summary>
/// <remarks>
/// Named rather than written as literals at both ends, because the producer
/// (<see cref="ScimBearerAuthenticationHandler"/>) and the consumer (the SCIM endpoints' domain check) are in
/// different files and a typo in either would fail OPEN — an unread scope claim is indistinguishable from an
/// unrestricted token.
/// </remarks>
internal static class ScimClaims
{
    /// <summary>One per email domain the authenticating token may provision into.</summary>
    internal const string AllowedEmailDomain = "scim_allowed_email_domain";

    /// <summary>
    /// The organization users provisioned through the authenticating token belong to. Absent when the
    /// token carries no binding, which leaves created users untagged.
    /// </summary>
    internal const string OrganizationId = "scim_organization_id";
}
