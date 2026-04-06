namespace Authagonal.Backup;

/// <summary>
/// Abstraction for writing backup data (filesystem, blob storage, etc.).
/// </summary>
public interface IBackupTarget
{
    /// <summary>Opens a writable stream for a backup file (e.g. "Users.jsonl.gz").</summary>
    Task<Stream> OpenWriteAsync(string backupId, string fileName, CancellationToken ct = default);

    /// <summary>Writes the backup manifest.</summary>
    Task WriteManifestAsync(string backupId, BackupManifest manifest, CancellationToken ct = default);

    /// <summary>Gets the last successful backup watermark (for incremental backups).</summary>
    Task<DateTimeOffset?> GetLastWatermarkAsync(CancellationToken ct = default);

    /// <summary>Updates the watermark after a successful backup.</summary>
    Task SetLastWatermarkAsync(DateTimeOffset watermark, CancellationToken ct = default);
}
