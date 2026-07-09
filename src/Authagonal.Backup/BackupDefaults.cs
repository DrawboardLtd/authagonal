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

    /// <summary>
    /// Tables ELIGIBLE for change-log-driven incremental reads (point-read each changed row) rather than a
    /// scan of the unindexed Timestamp column. A table belongs here only once its writes are fully change-
    /// log-captured (see TableUserStore's LogUpsert calls). Opt-in: pass this to <see cref="BackupOptions"/>
    /// to activate; the mechanism is off by default so shipping it can't silently miss rows changed by pre-
    /// capture code before a deliberate flip. Users is excluded: its login-state writes are not captured, so
    /// it stays on scan until a periodic full-scan backstop exists.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeLoggedTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "UserEmails", "UserFirstNames", "UserLastNames", "UserLogins", "UserExternalIds",
    };
}
