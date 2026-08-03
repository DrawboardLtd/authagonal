using System.Security.Cryptography;

namespace Authagonal.Backup;

/// <summary>
/// Opens a backup file ONCE and hands back a stream over exactly the bytes that were hashed.
/// </summary>
/// <remarks>
/// Shared by <see cref="RestoreService"/> and <see cref="MergeService"/>. It lives here rather than on either
/// of them because it was on one of them: the restore path verified its inputs and the rollup path did not, so
/// a tampered archive was laundered into a freshly hashed one by the retention job. A check that only one
/// reader performs is a check the other reader's callers do not get.
/// <para>
/// The file is read once. Verification used to hash the file and then reopen it to read the entities, so the
/// bytes that were checked and the bytes that were applied came from two separate reads of a target the
/// attacker is assumed to be able to write — which is the same attacker the hashes exist to stop. Swapping the
/// file between the two reads defeated the check entirely.
/// </para>
/// <para>
/// The verified copy is staged to a temp file rather than buffered in memory: a table file is unbounded, and a
/// restore that OOMs on a large deployment is a restore that does not happen. The staging file is created
/// owner-only and delete-on-close, because for the duration it holds the same credential material the archive
/// does.
/// </para>
/// </remarks>
public static class VerifiedRead
{
    /// <param name="expectedHash">
    /// The hash recorded for this file in its own backup's manifest, or null when there is nothing to check
    /// against — in which case the source stream is returned directly and no copy is made.
    /// </param>
    public static async Task<Stream?> OpenAsync(
        IBackupSource source, string backupId, string fileName, string? expectedHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var stream = await source.OpenReadAsync(backupId, fileName, ct);
        if (stream is null || expectedHash is null) return stream;

        var staged = new FileStream(
            Path.Combine(Path.GetTempPath(), $"authagonal-backup-{Guid.NewGuid():N}.tmp"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            await using (stream)
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await staged.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Backup integrity check failed: '{fileName}' hash does not match the manifest.");

            staged.Position = 0;
            return staged;
        }
        catch
        {
            await staged.DisposeAsync();
            throw;
        }
    }
}
