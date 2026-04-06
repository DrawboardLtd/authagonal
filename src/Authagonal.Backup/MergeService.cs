using System.IO.Compression;
using System.Text.Json;

namespace Authagonal.Backup;

/// <summary>
/// Merges a full backup with incremental backups and tombstones into a single current-state view.
/// Processes one table at a time to bound memory usage.
/// </summary>
public sealed class MergeService(IBackupSource source)
{
    /// <summary>
    /// Merges a full backup and incrementals into a new full backup written to the target.
    /// </summary>
    public async Task<BackupManifest> MergeToTargetAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        IBackupTarget target,
        bool gzip = true,
        CancellationToken ct = default)
    {
        var fullManifest = await source.ReadManifestAsync(fullBackupId, ct)
            ?? throw new InvalidOperationException($"Manifest not found for backup {fullBackupId}");

        // Collect all table names across full + incrementals
        var allTables = new HashSet<string>(fullManifest.Tables.Keys);
        foreach (var incrId in incrementalBackupIds)
        {
            var incrManifest = await source.ReadManifestAsync(incrId, ct);
            if (incrManifest?.Tables is not null)
            {
                foreach (var t in incrManifest.Tables.Keys)
                    allTables.Add(t);
            }
        }

        // Collect all tombstones from incrementals
        var tombstones = await LoadTombstonesAsync(incrementalBackupIds, ct);

        var backupStart = DateTimeOffset.UtcNow;
        var backupId = backupStart.ToString("yyyyMMdd-HHmmss");
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            BackupTimestamp = backupStart,
            Mode = "full",
            Compressed = gzip,
        };

        long totalEntities = 0;

        foreach (var tableName in allTables)
        {
            // Load full backup data for this table
            var entities = await LoadTableEntitiesAsync(fullBackupId, tableName, ct);

            // Apply incrementals in order
            foreach (var incrId in incrementalBackupIds)
            {
                var incrEntities = await LoadTableEntitiesAsync(incrId, tableName, ct);
                foreach (var (key, value) in incrEntities)
                {
                    entities[key] = value;
                }
            }

            // Apply tombstones
            if (tombstones.TryGetValue(tableName, out var tableTombstones))
            {
                foreach (var key in tableTombstones)
                {
                    entities.Remove(key);
                }
            }

            if (entities.Count == 0) continue;

            // Write merged table
            var ext = gzip ? ".jsonl.gz" : ".jsonl";
            var outputStream = await target.OpenWriteAsync(backupId, $"{tableName}{ext}", ct);
            Stream writeStream = gzip ? new GZipStream(outputStream, CompressionLevel.Optimal) : outputStream;
            await using var gzipScope = gzip ? writeStream : null;
            await using var writer = new StreamWriter(writeStream, System.Text.Encoding.UTF8);

            foreach (var jsonLine in entities.Values)
            {
                await writer.WriteLineAsync(jsonLine.AsMemory(), ct);
            }

            manifest.Tables[tableName] = new TableBackupInfo
            {
                EntityCount = entities.Count,
            };
            totalEntities += entities.Count;
        }

        manifest.TotalEntities = totalEntities;
        manifest.DurationSeconds = (DateTimeOffset.UtcNow - backupStart).TotalSeconds;
        await target.WriteManifestAsync(backupId, manifest, ct);

        return manifest;
    }

    /// <summary>
    /// Merges full + incrementals and writes the result to a stream as a single JSONL archive per table.
    /// The callback is invoked for each table with its name and a stream of JSONL lines.
    /// </summary>
    public async Task MergeToCallbackAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        Func<string, Stream, Task> onTable,
        CancellationToken ct = default)
    {
        var fullManifest = await source.ReadManifestAsync(fullBackupId, ct)
            ?? throw new InvalidOperationException($"Manifest not found for backup {fullBackupId}");

        var allTables = new HashSet<string>(fullManifest.Tables.Keys);
        foreach (var incrId in incrementalBackupIds)
        {
            var incrManifest = await source.ReadManifestAsync(incrId, ct);
            if (incrManifest?.Tables is not null)
            {
                foreach (var t in incrManifest.Tables.Keys)
                    allTables.Add(t);
            }
        }

        var tombstones = await LoadTombstonesAsync(incrementalBackupIds, ct);

        foreach (var tableName in allTables)
        {
            var entities = await LoadTableEntitiesAsync(fullBackupId, tableName, ct);

            foreach (var incrId in incrementalBackupIds)
            {
                var incrEntities = await LoadTableEntitiesAsync(incrId, tableName, ct);
                foreach (var (key, value) in incrEntities)
                {
                    entities[key] = value;
                }
            }

            if (tombstones.TryGetValue(tableName, out var tableTombstones))
            {
                foreach (var key in tableTombstones)
                {
                    entities.Remove(key);
                }
            }

            if (entities.Count == 0) continue;

            var ms = new MemoryStream();
            await using var writer = new StreamWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var jsonLine in entities.Values)
            {
                await writer.WriteLineAsync(jsonLine.AsMemory(), ct);
            }
            await writer.FlushAsync(ct);
            ms.Position = 0;

            await onTable(tableName, ms);
        }
    }

    private async Task<Dictionary<(string PK, string RK), string>> LoadTableEntitiesAsync(
        string backupId, string tableName, CancellationToken ct)
    {
        var entities = new Dictionary<(string, string), string>();

        // Try both compressed and uncompressed
        var stream = await source.OpenReadAsync(backupId, $"{tableName}.jsonl.gz", ct)
                     ?? await source.OpenReadAsync(backupId, $"{tableName}.jsonl", ct);

        if (stream is null) return entities;

        await using (stream)
        {
            Stream readStream = stream;
            // Auto-detect gzip
            var buffered = new BufferedStream(stream);
            var header = new byte[2];
            var read = await buffered.ReadAsync(header, ct);
            buffered.Position = 0;
            readStream = buffered;

            if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b)
            {
                readStream = new GZipStream(buffered, CompressionMode.Decompress);
            }

            await using var decompressScope = readStream != buffered ? readStream : null;
            using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var pk = doc.RootElement.GetProperty("PartitionKey").GetString()!;
                var rk = doc.RootElement.GetProperty("RowKey").GetString()!;
                entities[(pk, rk)] = line;
            }
        }

        return entities;
    }

    private async Task<Dictionary<string, HashSet<(string PK, string RK)>>> LoadTombstonesAsync(
        IReadOnlyList<string> incrementalBackupIds, CancellationToken ct)
    {
        var tombstones = new Dictionary<string, HashSet<(string, string)>>();

        foreach (var incrId in incrementalBackupIds)
        {
            var stream = await source.OpenReadAsync(incrId, "_tombstones.jsonl.gz", ct)
                         ?? await source.OpenReadAsync(incrId, "_tombstones.jsonl", ct);

            if (stream is null) continue;

            await using (stream)
            {
                Stream readStream = stream;
                var buffered = new BufferedStream(stream);
                var header = new byte[2];
                var read = await buffered.ReadAsync(header, ct);
                buffered.Position = 0;
                readStream = buffered;

                if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b)
                {
                    readStream = new GZipStream(buffered, CompressionMode.Decompress);
                }

                await using var decompressScope = readStream != buffered ? readStream : null;
                using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    using var doc = JsonDocument.Parse(line);
                    var table = doc.RootElement.GetProperty("Table").GetString()!;
                    var pk = doc.RootElement.GetProperty("PartitionKey").GetString()!;
                    var rk = doc.RootElement.GetProperty("RowKey").GetString()!;

                    if (!tombstones.TryGetValue(table, out var set))
                    {
                        set = new HashSet<(string, string)>();
                        tombstones[table] = set;
                    }
                    set.Add((pk, rk));
                }
            }
        }

        return tombstones;
    }
}
