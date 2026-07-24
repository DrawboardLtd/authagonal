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
    public bool MigrateRefreshTokens { get; set; }

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
