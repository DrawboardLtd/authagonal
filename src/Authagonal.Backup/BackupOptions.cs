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
}
