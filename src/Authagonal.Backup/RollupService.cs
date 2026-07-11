namespace Authagonal.Backup;

/// <summary>
/// Rolls up a full backup + incrementals into a new full backup, then cleans up the old ones.
/// </summary>
public sealed class RollupService(IBackupSource source, IBackupTarget target)
{
    /// <summary>
    /// Merges the full backup and all incrementals into a new full backup.
    /// Returns the new manifest. Caller is responsible for cleanup of old backups.
    /// <paramref name="newBackupId"/> names the result (null = timestamp id); see
    /// <see cref="MergeService.MergeToTargetAsync"/> for why retained snapshots must set it.
    /// </summary>
    public async Task<BackupManifest> RollupAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null)
    {
        var mergeService = new MergeService(source);
        return await mergeService.MergeToTargetAsync(fullBackupId, incrementalBackupIds, target, gzip, ct, newBackupId);
    }

    /// <summary>
    /// Performs rollup and then deletes the old full + incremental backups.
    /// </summary>
    public async Task<BackupManifest> RollupAndCleanAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null)
    {
        var newManifest = await RollupAsync(fullBackupId, incrementalBackupIds, gzip, ct, newBackupId);

        // Clean up old backups
        await source.DeleteBackupAsync(fullBackupId, ct);
        foreach (var incrId in incrementalBackupIds)
        {
            await source.DeleteBackupAsync(incrId, ct);
        }

        return newManifest;
    }
}
