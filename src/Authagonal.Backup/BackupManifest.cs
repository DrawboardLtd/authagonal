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

    /// <summary>
    /// Tables this run read via the change-log instead of a Timestamp scan. Null or empty means the run
    /// had full scan coverage (a full backup, a plain scan incremental, or a backstop scan) — which older
    /// manifests, written before this property existed, correctly deserialize to.
    /// </summary>
    public List<string>? ChangeLogTables { get; set; }
    public long TombstoneCount { get; set; }
    public long TotalEntities { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>
    /// SHA-256 hex hash of each backup file. Populated during backup, verified during restore.
    /// Key = filename (e.g. "Users.jsonl.gz"), Value = lowercase hex hash.
    /// </summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>
    /// HMAC-SHA256 over the serialized manifest (with this field cleared), hex-encoded. Null when the
    /// backup was written without a manifest key.
    /// </summary>
    /// <remarks>
    /// Without this, integrity verification is self-referential: <see cref="FileHashes"/> is the only
    /// thing authenticating the data files, and it sits in a plain JSON document beside them on the
    /// same target. Anyone who can rewrite a data file can rewrite its hash in the same breath, so the
    /// check detected corruption but not tampering — while the option that turns it on is called
    /// VerifyIntegrity. The MAC key lives outside the backup target, so an attacker holding only the
    /// target cannot forge it.
    /// </remarks>
    public string? ManifestMac { get; set; }

    /// <summary>
    /// The backup's content key, wrapped under the host's key-encryption key. Null for a plaintext
    /// archive.
    /// </summary>
    /// <remarks>
    /// Its presence is what tells restore the data files are encrypted, and it is covered by
    /// <see cref="ManifestMac"/> when a manifest key is configured — so an attacker cannot strip this
    /// field to make an encrypted archive look plaintext without also breaking the MAC.
    /// </remarks>
    public string? WrappedContentKey { get; set; }
}

public sealed class TableBackupInfo
{
    public long EntityCount { get; set; }
    public double DurationSeconds { get; set; }
}
