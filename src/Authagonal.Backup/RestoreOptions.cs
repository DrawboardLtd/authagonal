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
    /// If true, parse backup files but don't write anything.
    /// </summary>
    public bool DryRun { get; set; }

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
