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
