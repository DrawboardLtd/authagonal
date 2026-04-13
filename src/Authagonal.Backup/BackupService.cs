using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Backup;

[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Backup entity serialization uses heterogeneous Dictionary<string, object?> values")]
public sealed class BackupService(TableServiceClient serviceClient, IBackupTarget target, BackupOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<BackupManifest> RunAsync(CancellationToken ct = default)
    {
        var tables = options.Tables ?? BackupDefaults.Tables;
        var prefix = options.TablePrefix ?? "";

        // Determine incremental watermark
        DateTimeOffset? watermark = null;
        if (options.Incremental)
        {
            watermark = await target.GetLastWatermarkAsync(ct);
        }

        var backupStart = DateTimeOffset.UtcNow;
        var suffix = watermark.HasValue ? "-incr" : "";
        var backupId = backupStart.ToString("yyyyMMdd-HHmmss") + suffix;

        var sw = Stopwatch.StartNew();
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            BackupTimestamp = backupStart,
            Mode = watermark.HasValue ? "incremental" : "full",
            Compressed = options.Gzip,
            Watermark = watermark,
        };

        long totalEntities = 0;

        foreach (var tableName in tables)
        {
            var physicalName = prefix + tableName;
            var tableClient = serviceClient.GetTableClient(physicalName);
            var tableStart = Stopwatch.StartNew();

            string? filter = null;
            if (watermark.HasValue)
            {
                filter = $"Timestamp gt datetime'{watermark.Value:O}'";
            }

            long count = 0;
            StreamWriter? writer = null;
            Stream? outputStream = null;
            Stream? gzipStream = null;

            try
            {
                var pages = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1000, cancellationToken: ct);

                await foreach (var entity in pages)
                {
                    if (writer is null && !options.DryRun)
                    {
                        var ext = options.Gzip ? ".jsonl.gz" : ".jsonl";
                        outputStream = await target.OpenWriteAsync(backupId, $"{tableName}{ext}", ct);
                        if (options.Gzip)
                        {
                            gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal);
                            writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8);
                        }
                        else
                        {
                            writer = new StreamWriter(outputStream, System.Text.Encoding.UTF8);
                        }
                    }

                    if (!options.DryRun)
                    {
                        var dict = SerializeEntity(entity);
                        writer!.WriteLine(JsonSerializer.Serialize(dict, JsonOptions));
                    }

                    count++;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Table doesn't exist, skip
                continue;
            }
            finally
            {
                if (writer is not null) await writer.DisposeAsync();
                if (gzipStream is not null) await gzipStream.DisposeAsync();
                if (outputStream is not null) await outputStream.DisposeAsync();
            }

            tableStart.Stop();
            totalEntities += count;
            manifest.Tables[tableName] = new TableBackupInfo
            {
                EntityCount = count,
                DurationSeconds = tableStart.Elapsed.TotalSeconds
            };
        }

        // Back up tombstones for incremental backups
        long tombstoneCount = 0;
        if (watermark.HasValue)
        {
            tombstoneCount = await BackupTombstonesAsync(backupId, prefix, watermark.Value, ct);
        }

        sw.Stop();
        manifest.TombstoneCount = tombstoneCount;
        manifest.TotalEntities = totalEntities;
        manifest.DurationSeconds = sw.Elapsed.TotalSeconds;

        if (!options.DryRun)
        {
            await target.WriteManifestAsync(backupId, manifest, ct);
            await target.SetLastWatermarkAsync(backupStart, ct);
        }

        return manifest;
    }

    private async Task<long> BackupTombstonesAsync(string backupId, string prefix, DateTimeOffset watermark, CancellationToken ct)
    {
        var physicalName = prefix + "Tombstones";
        var tableClient = serviceClient.GetTableClient(physicalName);

        var filter = $"Timestamp gt datetime'{watermark:O}'";
        long count = 0;
        StreamWriter? writer = null;
        Stream? outputStream = null;
        Stream? gzipStream = null;

        try
        {
            var pages = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1000, cancellationToken: ct);

            await foreach (var entity in pages)
            {
                if (writer is null && !options.DryRun)
                {
                    var ext = options.Gzip ? ".jsonl.gz" : ".jsonl";
                    outputStream = await target.OpenWriteAsync(backupId, $"_tombstones{ext}", ct);
                    if (options.Gzip)
                    {
                        gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal);
                        writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8);
                    }
                    else
                    {
                        writer = new StreamWriter(outputStream, System.Text.Encoding.UTF8);
                    }
                }

                if (!options.DryRun)
                {
                    // Tombstone format: Table (from PK), PK|RK (from RK), DeletedAt
                    var rk = entity.RowKey;
                    var pipeIndex = rk.IndexOf('|');
                    var tombstone = new Dictionary<string, object?>
                    {
                        ["Table"] = entity.PartitionKey,
                        ["PartitionKey"] = pipeIndex >= 0 ? rk[..pipeIndex] : rk,
                        ["RowKey"] = pipeIndex >= 0 ? rk[(pipeIndex + 1)..] : "",
                        ["DeletedAt"] = entity.GetDateTimeOffset("DeletedAt"),
                    };
                    writer!.WriteLine(JsonSerializer.Serialize(tombstone, JsonOptions));
                }

                count++;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Tombstones table doesn't exist — no deletes tracked
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
            if (gzipStream is not null) await gzipStream.DisposeAsync();
            if (outputStream is not null) await outputStream.DisposeAsync();
        }

        return count;
    }

    internal static Dictionary<string, object?> SerializeEntity(TableEntity entity)
    {
        var dict = new Dictionary<string, object?>
        {
            ["PartitionKey"] = entity.PartitionKey,
            ["RowKey"] = entity.RowKey,
            ["Timestamp"] = entity.Timestamp,
            ["ETag"] = entity.ETag.ToString(),
        };

        foreach (var kvp in entity)
        {
            if (kvp.Key is "odata.etag") continue;
            dict.TryAdd(kvp.Key, kvp.Value);
        }

        return dict;
    }
}
