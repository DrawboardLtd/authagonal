namespace Authagonal.Backup;

public sealed class RestoreOptions
{
    /// <summary>
    /// Tables to restore. If null, restores all tables found in the backup.
    /// </summary>
    public string[]? Tables { get; set; }

    /// <summary>
    /// Table name prefix for multi-tenant storage (e.g. "acmecorp").
    /// </summary>
    public string? TablePrefix { get; set; }

    /// <summary>
    /// Restore mode: Upsert (default), Merge, or Clean (delete all then restore).
    /// </summary>
    public RestoreMode Mode { get; set; } = RestoreMode.Upsert;

    /// <summary>
    /// If true, parse backup files but don't write anything — and, critically, don't DELETE anything
    /// either. <see cref="RestoreMode.Clean"/> honours this too; a dry run that emptied the target tables
    /// would be the most destructive operation the tool offers.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// PartitionKey prefix that scopes a <see cref="RestoreMode.Clean"/> wipe, matching
    /// <c>EnvPartitioner</c>'s <c>{env}|</c> scheme (e.g. <c>"sandbox-3|"</c>). When set, Clean deletes
    /// only rows in that env; when null, Clean empties the whole physical table.
    ///
    /// Sandbox envs share one set of tables, so an unscoped Clean restore of one env's backup destroys
    /// every sibling env in the same table. Leave null only when the table holds a single env (the live
    /// tables), where a whole-table wipe is what Clean is expected to mean.
    /// </summary>
    public string? CleanEnvPrefix { get; set; }

    /// <summary>
    /// Permit <see cref="RestoreMode.Clean"/> against a backup whose manifest records
    /// <c>Mode == "incremental"</c>. Refused by default: an incremental contains only the rows that
    /// changed since its watermark, so emptying the table first leaves it holding just that delta — every
    /// unchanged row is destroyed. The correct sequence is a Clean restore of the parent full followed by
    /// Upsert restores of the incrementals. Set this only when you genuinely intend the delta to be the
    /// entire resulting dataset.
    /// </summary>
    public bool AllowCleanFromIncremental { get; set; }

    /// <summary>
    /// Verify each data file's SHA-256 against the manifest's recorded hash before applying any of
    /// its entities. On (default) true a hash mismatch, or a data file absent from the manifest,
    /// aborts the restore — preventing a tampered backup from injecting entities (e.g. an admin
    /// client, a reset password hash, or an attacker-controlled signing key → token forgery).
    /// Backups written before integrity hashing existed (no FileHashes in the manifest) cannot be
    /// verified and are allowed through with a warning.
    /// </summary>
    public bool VerifyIntegrity { get; set; } = true;

    /// <summary>
    /// Apply the backup's <c>_tombstones</c> file after the data files: delete each recorded key from
    /// the restored tables. On by default — an incremental's deletes are as much a part of its state
    /// as its upserts, and skipping them resurrects deleted (incl. GDPR-erased) rows when restoring a
    /// full + incrementals sequence. Only meaningful when restoring incrementals (fulls carry no
    /// tombstone file). Disable to inspect deleted rows deliberately.
    /// </summary>
    public bool ApplyTombstones { get; set; } = true;
}

public enum RestoreMode
{
    /// <summary>Insert or replace each entity.</summary>
    Upsert,
    /// <summary>Insert or merge (preserve existing properties not in backup).</summary>
    Merge,
    /// <summary>Delete all existing entities before restoring.</summary>
    Clean
}
