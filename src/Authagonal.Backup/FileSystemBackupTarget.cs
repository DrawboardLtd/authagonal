using System.Text.Json;

namespace Authagonal.Backup;

public sealed class FileSystemBackupTarget(string rootDirectory) : IBackupTarget
{
    public Task<Stream> OpenWriteAsync(string backupId, string fileName, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootDirectory, backupId);
        Directory.CreateDirectory(dir);
        var stream = (Stream)new FileStream(Path.Combine(dir, fileName), FileMode.Create, FileAccess.Write);
        return Task.FromResult(stream);
    }

    public async Task WriteManifestAsync(string backupId, BackupManifest manifest, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootDirectory, backupId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "_manifest.json");
        var json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);
        await File.WriteAllTextAsync(path, json, ct);
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
