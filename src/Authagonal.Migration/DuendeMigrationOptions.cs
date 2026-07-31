namespace Authagonal.Migration;

/// <summary>
/// Bound from the <c>Migration</c> configuration section. Controls the one-time Duende → Authagonal
/// migration engine and its hosted runner.
/// </summary>
public sealed class DuendeMigrationOptions
{
    public const string SectionName = "Migration";

    /// <summary>Master switch. When false the hosted runner is a no-op (nothing reads the source DB).</summary>
    public bool Enabled { get; set; }

    /// <summary>Source Duende SQL Server connection.</summary>
    public SourceOptions Source { get; set; } = new();

    /// <summary>Validate + report only; write nothing. This is the whole "validate" pass.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Marker RowKey. Bump to re-run (e.g. a cutover delta sweep). Only a <c>Completed</c>,
    /// non-<see cref="DryRun"/> marker for the current version blocks a re-run.
    /// </summary>
    public string Version { get; set; } = "1";

    /// <summary>How to treat a user whose id already exists in the target.</summary>
    public UsersMode UsersMode { get; set; } = UsersMode.CreateOnly;

    /// <summary>Migrate OAuth clients (config-seeded clients always win; existing are skipped).</summary>
    public bool MigrateClients { get; set; } = true;

    /// <summary>Migrate live refresh tokens. Off by default — cutover forces one re-login.</summary>
    /// <remarks>
    /// Requires <see cref="SourceGrantKeysAreUnhashed"/>. Stock Duende cannot satisfy it — see there.
    /// </remarks>
    public bool MigrateRefreshTokens { get; set; }

    /// <summary>
    /// Asserts that the source PersistedGrants.Key column holds refresh-token handles verbatim rather
    /// than Duende's hash of them. Only a fork with a custom grant store can set this truthfully.
    /// </summary>
    /// <remarks>
    /// Stock Duende stores base64(SHA-256(handle + ":" + grantType)) and hashes the presented handle
    /// again on lookup, so the handle is not recoverable and live tokens cannot be migrated at all.
    /// Without this assertion the refresh-token pass is skipped with a warning rather than writing
    /// rows that look migrated and are permanently unredeemable.
    /// </remarks>
    public bool SourceGrantKeysAreUnhashed { get; set; }

    /// <summary>
    /// Bounded write concurrency for the high-volume passes (users, external logins, MFA, refresh
    /// tokens). Entities are independent (Table storage has no referential integrity), so writes fan
    /// out; the bound keeps us under Azure Table's per-account throughput ceiling and off hot shared
    /// index partitions (e.g. the email-domain index). Dial down for small/throttle-prone accounts;
    /// 1 restores fully sequential behaviour.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 32;

    /// <summary>Give up waiting for cluster leadership after this many minutes; a later restart retries.</summary>
    public int LeaseWaitMinutes { get; set; } = 10;

    /// <summary>Delay before the runner starts, so seed services finish and startup is unblocked.</summary>
    public int StartupDelaySeconds { get; set; } = 30;

    public sealed class SourceOptions
    {
        public string? ConnectionString { get; set; }
    }
}

/// <summary>How the users pass treats an id that already exists in the target store.</summary>
public enum UsersMode
{
    /// <summary>Create; on conflict count as skipped (the existing record wins). Safe for delta sweeps.</summary>
    CreateOnly,

    /// <summary>Create; on conflict update. NEVER use post-cutover — clobbers rehashed passwords / new MFA.</summary>
    Upsert
}
