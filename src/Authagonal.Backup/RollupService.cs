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
    /// <param name="encryptionKey">
    /// The key-encryption key the inputs were written with; the rollup is written with it too.
    /// Required when any input is encrypted — a retention job that quietly produced a plaintext
    /// snapshot from encrypted inputs would be performing the downgrade itself, on the copy that
    /// outlives everything it was rolled up from.
    /// </param>
    public async Task<BackupManifest> RollupAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null,
        byte[]? encryptionKey = null)
    {
        var mergeService = new MergeService(source);
        return await mergeService.MergeToTargetAsync(
            fullBackupId, incrementalBackupIds, target, gzip, ct, newBackupId, encryptionKey);
    }

    /// <summary>
    /// Performs rollup and then deletes the old full + incremental backups.
    /// </summary>
    public async Task<BackupManifest> RollupAndCleanAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null,
        byte[]? encryptionKey = null)
    {
        var newManifest = await RollupAsync(fullBackupId, incrementalBackupIds, gzip, ct, newBackupId, encryptionKey);

        // Clean up old backups
        await source.DeleteBackupAsync(fullBackupId, ct);
        foreach (var incrId in incrementalBackupIds)
        {
            await source.DeleteBackupAsync(incrId, ct);
        }

        return newManifest;
    }
}
