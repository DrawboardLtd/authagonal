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
    /// <param name="manifestKey">
    /// The manifest HMAC key. Supplying it authenticates every input before it is read and signs the merged
    /// manifest — required for a snapshot that is meant to stay restorable, because the rollup output was
    /// previously the one copy carrying no MAC while <see cref="RollupAndCleanAsync"/> deleted the signed
    /// originals. See <see cref="MergeService.MergeToTargetAsync"/>.
    /// </param>
    public async Task<BackupManifest> RollupAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null,
        byte[]? encryptionKey = null,
        byte[]? manifestKey = null)
    {
        var mergeService = new MergeService(source);
        return await mergeService.MergeToTargetAsync(
            fullBackupId, incrementalBackupIds, target, gzip, ct, newBackupId, encryptionKey, manifestKey);
    }

    /// <summary>
    /// Performs rollup and then deletes the old full + incremental backups.
    /// </summary>
    /// <remarks>
    /// This deletes the signed originals, so it is the call that makes <paramref name="manifestKey"/> matter
    /// most: without one, the surviving copy is unauthenticated and the only route back to it is
    /// <c>AllowUnauthenticatedManifest</c>.
    /// </remarks>
    public async Task<BackupManifest> RollupAndCleanAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null,
        byte[]? encryptionKey = null,
        byte[]? manifestKey = null)
    {
        var newManifest = await RollupAsync(
            fullBackupId, incrementalBackupIds, gzip, ct, newBackupId, encryptionKey, manifestKey);

        // Clean up old backups
        await source.DeleteBackupAsync(fullBackupId, ct);
        foreach (var incrId in incrementalBackupIds)
        {
            await source.DeleteBackupAsync(incrId, ct);
        }

        return newManifest;
    }
}
