using System.Text.Json;

namespace Authagonal.Backup;

public sealed class FileSystemBackupTarget(string rootDirectory) : IBackupTarget
{
    /// <summary>
    /// Owner-only, on both the directory and every file inside it.
    /// </summary>
    /// <remarks>
    /// These files were created with the process umask, which on a typical host means world-readable.
    /// A backup is not ordinary data: it carries MFA TOTP seeds — directly replayable second factors —
    /// alongside every password hash, client secret hash and recovery-code hash in the deployment, all
    /// offline-crackable at the attacker's leisure. Anyone with a shell on the box could read the lot
    /// without touching the identity provider or leaving a trace in it.
    /// <para>
    /// Not encryption. Envelope encryption of the archive is the real answer and is a format change;
    /// this is the part that costs nothing and removes the most common way these files get read.
    /// </para>
    /// </remarks>
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static string EnsureDirectory(string root, string backupId)
    {
        var dir = Path.Combine(root, BackupPath.Safe(backupId, nameof(backupId)));
        if (!Directory.Exists(dir))
        {
            // Set at creation rather than after: a chmod that follows the mkdir leaves a window in
            // which the directory is readable.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, OwnerOnlyDirectory);
        }
        return dir;
    }

    private static Stream CreateFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Create, FileAccess.Write);

        // Same reasoning: the mode is part of the create, so the file is never briefly world-readable.
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerOnlyFile,
        });
    }

    public Task<Stream> OpenWriteAsync(string backupId, string fileName, CancellationToken ct = default)
    {
        var dir = EnsureDirectory(rootDirectory, backupId);
        var stream = CreateFile(Path.Combine(dir, BackupPath.Safe(fileName, nameof(fileName))));
        return Task.FromResult(stream);
    }

    public async Task WriteManifestAsync(string backupId, BackupManifest manifest, CancellationToken ct = default)
    {
        var dir = EnsureDirectory(rootDirectory, backupId);
        var json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);

        // Through the same owner-only create as the data files. The manifest carries the file hashes
        // and, when configured, their MAC — writing it world-readable would hand an attacker the
        // integrity metadata for the archive it sits beside.
        await using var stream = CreateFile(Path.Combine(dir, "_manifest.json"));
        await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
        await writer.WriteAsync(json.AsMemory(), ct);
    }

    public Task<DateTimeOffset?> GetLastWatermarkAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(rootDirectory, ".lastbackup");
        if (!File.Exists(path))
            return Task.FromResult<DateTimeOffset?>(null);

        var text = File.ReadAllText(path).Trim();
        if (DateTimeOffset.TryParse(text, out var parsed))
            return Task.FromResult<DateTimeOffset?>(parsed);

        return Task.FromResult<DateTimeOffset?>(null);
    }

    public async Task SetLastWatermarkAsync(DateTimeOffset watermark, CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootDirectory);
        await File.WriteAllTextAsync(Path.Combine(rootDirectory, ".lastbackup"), watermark.ToString("O"), ct);
    }
}
