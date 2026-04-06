namespace Authagonal.Backup;

public static class BackupDefaults
{
    /// <summary>
    /// All Authagonal data tables (20 tables). Excludes transient tables (SamlReplayCache, OidcStateStore)
    /// and the Tombstones table (handled separately by the backup engine).
    /// </summary>
    public static readonly string[] Tables =
    [
        "Users", "UserEmails", "UserLogins", "UserExternalIds",
        "Clients",
        "Grants", "GrantsBySubject", "GrantsByExpiry",
        "SigningKeys",
        "SsoDomains",
        "SamlProviders", "OidcProviders",
        "UserProvisions",
        "MfaCredentials", "MfaChallenges", "MfaWebAuthnIndex",
        "ScimTokens", "ScimGroups", "ScimGroupExternalIds",
        "Roles"
    ];
}
