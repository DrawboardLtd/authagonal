namespace Authagonal.Backup;

public static class BackupDefaults
{
    /// <summary>
    /// All Authagonal data tables. Excludes transient tables (SamlReplayCache, OidcStateStore,
    /// RevokedTokens — entries are bounded by access token lifetime, typically minutes)
    /// and the Tombstones table (handled separately by the backup engine).
    /// </summary>
    public static readonly string[] Tables =
    [
        "Users", "UserEmails", "UserFirstNames", "UserLastNames", "UserLogins", "UserExternalIds",
        "Clients",
        "Grants", "GrantsBySubject", "GrantsByExpiry",
        "SigningKeys",
        "SsoDomains",
        "SamlProviders", "OidcProviders",
        "UserProvisions",
        "MfaCredentials", "MfaChallenges", "MfaWebAuthnIndex",
        "ScimTokens", "ScimGroups", "ScimGroupExternalIds",
        "Roles",
        "Scopes"
    ];
}
