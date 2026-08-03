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
    /// <remarks>
    /// Three tables the provider creates were missing, and none of the absences was fail-safe — a restored
    /// deployment came up with authorization QUIETLY WEAKER than the one that was backed up:
    /// <list type="bullet">
    /// <item><c>AgentProfiles</c> holds the admin-configured agent ceiling, mode, delegation depth, token
    /// lifetime cap and high-risk default. <c>ProtocolTokenService</c> looks the profile up and, absent one,
    /// "behaves exactly as it always has" — every gate lives inside <c>if (agentProfile is not null)</c>. So
    /// after a rebuild the agent client authenticated with its restored secret and its exchange took the
    /// unprofiled path: no standing-consent requirement, no ceiling intersection, no approval parking, no
    /// depth budget, no <c>act</c> chain for audit to see.</item>
    /// <item><c>UserRoles</c> holds role assignments. Restoring <c>Roles</c> without them leaves the roles
    /// defined and nobody holding them.</item>
    /// <item><c>UpstreamRefreshTokens</c> was already named in <see cref="SecretBearingTables"/> as though it
    /// were in the archive, which is how the omission hid: the inventory the CLI prints described a table the
    /// backup never wrote.</item>
    /// </list>
    /// Kept in sync with the provider's own table set by <c>BackupTableCoverageTests</c> — the list is a claim
    /// about "all data tables" and only a comparison against what is actually created can keep it true.
    /// </remarks>
    public static readonly string[] Tables =
    [
        "Users", "UserEmails", "UserFirstNames", "UserLastNames", "UserLogins", "UserExternalIds",
        "UserEmailDomains", "UserEmailLocalPrefixes",
        "Clients",
        "Grants", "GrantsBySubject", "GrantsByExpiry",
        "SigningKeys",
        "SsoDomains",
        "SamlProviders", "OidcProviders",
        "UpstreamRefreshTokens",
        "UserProvisions",
        "MfaCredentials", "MfaChallenges", "MfaWebAuthnIndex",
        "ScimTokens", "ScimGroups", "ScimGroupExternalIds", "ScimGroupRoleMappings",
        "Roles", "UserRoles",
        "Scopes",
        "AgentProfiles",
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

    /// <summary>
    /// Tables whose rows carry credential material, and what each one exposes if the archive is read.
    /// </summary>
    /// <remarks>
    /// The <c>SigningKeys</c> warning has always been explicit, and it is not the only table that
    /// warrants one. The archive is plaintext JSONL: MfaCredentials in particular holds TOTP seeds,
    /// which are DIRECTLY REPLAYABLE — whoever reads one generates that user's second factor
    /// indefinitely, with nothing to detect it and no rotation short of re-enrolling the user. The
    /// hashes are not equivalent to that, but they are offline-crackable at the attacker's leisure
    /// against a wordlist, which is a different proposition from being crackable against a rate-limited
    /// login endpoint.
    /// <para>
    /// Named here so the CLI can print an inventory before it writes, rather than leaving an operator
    /// to infer what "back up the identity provider" put on disk.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> SecretBearingTables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MfaCredentials"] = "TOTP seeds (directly replayable second factors) and recovery-code hashes",
            ["Users"] = "password hashes, offline-crackable",
            ["Clients"] = "client secret hashes, offline-crackable",
            ["OidcProviders"] = "upstream IdP client secrets (plaintext under the default secret provider)",
            ["SigningKeys"] = "the JWT signing PRIVATE key — excluded unless IncludeSigningKeys is set",
            ["UpstreamRefreshTokens"] = "live refresh tokens for upstream identity providers",
            ["ScimTokens"] = "SCIM provisioning bearer-token hashes",
        };
}
