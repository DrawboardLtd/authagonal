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

    /// <summary>
    /// When set (with <see cref="Incremental"/>), use this watermark for the run instead of the target's
    /// stored one. This is how the periodic full-scan backstop works: pass the timestamp of the last
    /// full-coverage scan (with <see cref="ChangeLoggedTables"/> unset) and the run re-scans the whole
    /// window on the Timestamp column, catching rows the change-log never captured — login-state writes,
    /// non-store writers, pre-capture pods during a deploy. Ignored for full (non-incremental) backups.
    /// </summary>
    public DateTimeOffset? WatermarkOverride { get; set; }

    /// <summary>
    /// Safety margin subtracted from the watermark before every Timestamp filter (see
    /// <see cref="BackupDefaults.WatermarkSkewMargin"/> for why). Override only in tests that assert
    /// exact window boundaries; production callers keep the default.
    /// </summary>
    public TimeSpan WatermarkSkewMargin { get; set; } = BackupDefaults.WatermarkSkewMargin;

    /// <summary>
    /// Key used to MAC the manifest. When set, the manifest is signed so a restore holding the same
    /// key can prove the recorded file hashes were not rewritten along with the files.
    /// </summary>
    public byte[]? ManifestKey { get; set; }

    /// <summary>
    /// 32-byte key-encryption key. When set, every data file is encrypted with AES-256-GCM under a
    /// per-backup content key, and that content key is wrapped under this one and recorded in the
    /// manifest.
    /// </summary>
    /// <remarks>
    /// Resolve it from wherever the deployment keeps key material — Key Vault, KMS, Vault — and NOT
    /// from the backup target, or the envelope protects nothing. Without it the archive is plaintext
    /// JSONL: MFA TOTP seeds (directly replayable second factors, with no rotation short of
    /// re-enrolling the user) alongside every password hash, client secret hash and recovery-code hash
    /// in the deployment, offline-crackable at leisure.
    /// </remarks>
    public byte[]? EncryptionKey { get; set; }
}
