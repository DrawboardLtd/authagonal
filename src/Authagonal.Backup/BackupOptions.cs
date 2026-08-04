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
    /// Which run's watermark this configuration owns. Null when the run covers the default table set with no
    /// prefix, so a plain schedule keeps using the unscoped <c>.lastbackup</c>.
    /// </summary>
    /// <remarks>
    /// The watermark was one value per output directory, advanced by any successful run. Two documented option
    /// combinations broke on that. <c>--prefix</c> exists for multi-tenancy, so a second tenant's FIRST EVER
    /// incremental into the same <c>--output</c> found a recent watermark, skipped the full-backup fallback, and
    /// produced an archive covering only the last interval. And <c>--tables</c> restricted a run to a subset
    /// while advancing the watermark for every table, so the next full-set incremental skipped everything
    /// committed to the others before it.
    /// <para>
    /// Derived from the prefix and the table set — the two things that decide what a run actually covered. A
    /// digest rather than the values themselves because it becomes a filename, and a table list can be long
    /// and contain characters a path will not take.
    /// </para>
    /// </remarks>
    public string? WatermarkScope()
    {
        var tables = Tables is { Length: > 0 }
            ? string.Join(',', Tables.OrderBy(t => t, StringComparer.Ordinal))
            : "";

        if (string.IsNullOrEmpty(TablePrefix) && tables.Length == 0)
            return null;

        var material = $"{TablePrefix}\u0000{tables}";
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material));

        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

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
