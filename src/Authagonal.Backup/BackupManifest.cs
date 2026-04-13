using System.Text.Json.Serialization;

namespace Authagonal.Backup;

public sealed class BackupManifest
{
    public string BackupId { get; set; } = "";
    public DateTimeOffset BackupTimestamp { get; set; }
    public string Mode { get; set; } = "full"; // "full" or "incremental"
    public bool Compressed { get; set; }
    public DateTimeOffset? Watermark { get; set; }
    public string? ParentBackupId { get; set; }
    public Dictionary<string, TableBackupInfo> Tables { get; set; } = new();
    public long TombstoneCount { get; set; }
    public long TotalEntities { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>
    /// SHA-256 hex hash of each backup file. Populated during backup, verified during restore.
    /// Key = filename (e.g. "Users.jsonl.gz"), Value = lowercase hex hash.
    /// </summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();
}

public sealed class TableBackupInfo
{
    public long EntityCount { get; set; }
    public double DurationSeconds { get; set; }
}
