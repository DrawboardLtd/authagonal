using System.Text.Json;

namespace Authagonal.Backup;

public sealed class FileSystemBackupSource(string rootDirectory) : IBackupSource
{
    public async Task<BackupManifest?> ReadManifestAsync(string backupId, CancellationToken ct = default)
    {
        var path = Path.Combine(rootDirectory, backupId, "_manifest.json");
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize(json, BackupJsonContext.Default.BackupManifest);
    }

    public Task<Stream?> OpenReadAsync(string backupId, string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(rootDirectory, backupId, fileName);
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read));
    }

    public Task<IReadOnlyList<string>> ListBackupIdsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(rootDirectory))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var dirs = Directory.GetDirectories(rootDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.StartsWith("."))
            .OrderBy(name => name)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(dirs!);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string backupId, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootDirectory, backupId);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files!);
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootDirectory, backupId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }
}
