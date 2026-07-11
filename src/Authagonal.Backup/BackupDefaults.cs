namespace Authagonal.Backup;

public static class BackupDefaults
{
    /// <summary>
    /// Safety margin subtracted from the incremental watermark before filtering on the storage-stamped
    /// <c>Timestamp</c> column. The watermark is pod-clock <c>UtcNow</c> while row Timestamps are
    /// storage-clock; a mutation committing inside the skew window would otherwise be excluded by this
    /// run AND every later run (each filters <c>Timestamp gt</c> a later watermark) — silently missing
    /// from all backups. Re-reading the margin costs a few duplicate rows per run (upsert-merge dedupes);
    /// callers that purge the change-log after a backup must bound the purge by the SAME margin or they
    /// delete rows the next run still needs.
    /// </summary>
    public static readonly TimeSpan WatermarkSkewMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// All Authagonal data tables. Excludes transient tables (SamlReplayCache, OidcStateStore,
    /// RevokedTokens — entries are bounded by access token lifetime, typically minutes)
    /// and the Tombstones table (handled separately by the backup engine).
    /// </summary>
    public static readonly string[] Tables =
    [
        "Users", "UserEmails", "UserFirstNames", "UserLastNames", "UserLogins", "UserExternalIds",
        "UserEmailDomains", "UserEmailLocalPrefixes",
        "Clients",
        "Grants", "GrantsBySubject", "GrantsByExpiry",
        "SigningKeys",
        "SsoDomains",
        "SamlProviders", "OidcProviders",
        "UserProvisions",
        "MfaCredentials", "MfaChallenges", "MfaWebAuthnIndex",
        "ScimTokens", "ScimGroups", "ScimGroupExternalIds", "ScimGroupRoleMappings",
        "Roles",
        "Scopes",
        "ProvisioningApps"
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
        "UserEmailDomains", "UserEmailLocalPrefixes",
        "ScimGroupRoleMappings", "ProvisioningApps",
    };

    /// <summary>
    /// <see cref="ChangeLoggedTables"/> plus Users — the biggest table, and the whole point of the
    /// optimization. Users' profile upserts ARE change-log-captured, but its login-state writes
    /// (RecordSuccessful/FailedLogin) deliberately are not (hot-path, low-value fields), so this set is
    /// only safe when the caller ALSO runs a periodic full-scan backstop: an incremental with
    /// <see cref="BackupOptions.WatermarkOverride"/> set to the last full-coverage scan and the
    /// change-log path off. Without the backstop, login-state changes never reach a backup.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeLoggedTablesWithUsers = new HashSet<string>(
        ChangeLoggedTables, StringComparer.Ordinal)
    {
        "Users",
    };
}
