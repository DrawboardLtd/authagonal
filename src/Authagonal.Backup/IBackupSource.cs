namespace Authagonal.Backup;

/// <summary>
/// Abstraction for reading backup data (filesystem, blob storage, etc.).
/// </summary>
public interface IBackupSource
{
    /// <summary>Reads the manifest for a backup.</summary>
    Task<BackupManifest?> ReadManifestAsync(string backupId, CancellationToken ct = default);

    /// <summary>Opens a readable stream for a backup file (e.g. "Users.jsonl.gz").</summary>
    Task<Stream?> OpenReadAsync(string backupId, string fileName, CancellationToken ct = default);

    /// <summary>Lists all backup IDs, ordered chronologically (oldest first).</summary>
    Task<IReadOnlyList<string>> ListBackupIdsAsync(CancellationToken ct = default);

    /// <summary>Lists file names in a backup (e.g. "Users.jsonl.gz", "_tombstones.jsonl.gz").</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string backupId, CancellationToken ct = default);

    /// <summary>Deletes a backup and all its files.</summary>
    Task DeleteBackupAsync(string backupId, CancellationToken ct = default);
}
