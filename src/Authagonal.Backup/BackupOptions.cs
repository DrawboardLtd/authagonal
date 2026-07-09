namespace Authagonal.Backup;

public sealed class BackupOptions
{
    /// <summary>
    /// Tables to back up. If null, uses <see cref="BackupDefaults.Tables"/>.
    /// </summary>
    public string[]? Tables { get; set; }

    /// <summary>
    /// Table name prefix for multi-tenant storage (e.g. "acmecorp").
    /// </summary>
    public string? TablePrefix { get; set; }

    /// <summary>
    /// Whether to perform an incremental backup (only entities changed since last watermark).
    /// </summary>
    public bool Incremental { get; set; }

    /// <summary>
    /// Whether to gzip-compress output files.
    /// </summary>
    public bool Gzip { get; set; }

    /// <summary>
    /// If true, enumerate entities but don't write anything.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Whether to include the <c>SigningKeys</c> table. Off by default: for hosts using the local
    /// (table-stored) key source this table contains the JWT signing PRIVATE key, so a plaintext
    /// backup file would let anyone who reads it forge tokens. Only enable when the backup target is
    /// itself encrypted/access-controlled. (Vault Transit hosts keep no private key in the table.)
    /// </summary>
    public bool IncludeSigningKeys { get; set; }

    /// <summary>
    /// Tables whose incremental backup reads from the change-log instead of scanning the live table. Null
    /// (and empty) mean all-scan; pass <see cref="BackupDefaults.ChangeLoggedTables"/> to activate the
    /// change-log path for the eligible tables. Opt-in so the mechanism ships inert until a deliberate flip.
    /// </summary>
    public IReadOnlySet<string>? ChangeLoggedTables { get; set; }
}
