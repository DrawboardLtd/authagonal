using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
        // Exclude the signing-key table by default — for local-key-source hosts its rows hold the
        // JWT signing private key, which must not be written to a plaintext backup file.
        if (!options.IncludeSigningKeys)
            tables = tables.Where(t => !string.Equals(t, "SigningKeys", StringComparison.OrdinalIgnoreCase)).ToArray();
        var prefix = options.TablePrefix ?? "";

        // Determine incremental watermark. WatermarkOverride wins: a backstop scan passes the last
        // full-coverage scan's timestamp so the run covers the whole window since it, not just the hour
        // since the stored (per-run) watermark.
        DateTimeOffset? watermark = null;
        if (options.Incremental)
        {
            watermark = options.WatermarkOverride ?? await target.GetLastWatermarkAsync(ct);
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

        // Incremental reads for change-logged tables come from the change-log (physically the Tombstones
        // table), not a Timestamp scan of the live table. Opt-in: null means all-scan (the caller passes
        // BackupDefaults.ChangeLoggedTables to activate). Defaulting OFF keeps shipping the mechanism inert
        // until a deliberate flip, so a deploy can't silently miss rows changed by pre-capture code.
        var changeLogged = options.ChangeLoggedTables ?? (IReadOnlySet<string>)new HashSet<string>();
        var changeLogClient = serviceClient.GetTableClient(prefix + "Tombstones");

        foreach (var tableName in tables)
        {
            var physicalName = prefix + tableName;
            var tableClient = serviceClient.GetTableClient(physicalName);
            var tableStart = Stopwatch.StartNew();

            long count = 0;
            string? fileName = null;
            StreamWriter? writer = null;
            Stream? outputStream = null;
            Stream? gzipStream = null;
            HashingStream? hashingStream = null;

            try
            {
                // Incremental read for a change-logged table: enumerate its Op="U" change-log entries since
                // the watermark and point-read each live row, rather than scanning the whole table on the
                // unindexed Timestamp column. Full backups (and tables still on the scan path) scan. Deletes
                // are captured separately by the tombstone pass below.
                IAsyncEnumerable<TableEntity> pages;
                if (watermark.HasValue && changeLogged.Contains(tableName))
                {
                    (manifest.ChangeLogTables ??= []).Add(tableName);
                    pages = ReadUpsertsViaChangeLogAsync(changeLogClient, tableClient, tableName, watermark.Value, ct);
                }
                else
                {
                    var filter = watermark.HasValue ? $"Timestamp gt datetime'{watermark.Value:O}'" : null;
                    pages = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1000, cancellationToken: ct);
                }

                await foreach (var entity in pages)
                {
                    if (writer is null && !options.DryRun)
                    {
                        var ext = options.Gzip ? ".jsonl.gz" : ".jsonl";
                        fileName = $"{tableName}{ext}";
                        outputStream = await target.OpenWriteAsync(backupId, fileName, ct);
                        // Hash the exact bytes written to the target so restore can verify integrity.
                        hashingStream = new HashingStream(outputStream);
                        // leaveOpen: true on both layers — otherwise disposing the writer cascades
                        // (writer → gzip → hashingStream → IncrementalHash), disposing the hash BEFORE
                        // GetHashHex() reads it below, which throws ObjectDisposedException. We dispose
                        // hashingStream explicitly after reading the hash.
                        if (options.Gzip)
                        {
                            gzipStream = new GZipStream(hashingStream, CompressionLevel.Optimal, leaveOpen: true);
                            writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
                        }
                        else
                        {
                            writer = new StreamWriter(hashingStream, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
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
                if (hashingStream is not null)
                {
                    // writer + gzip are disposed, so every byte has flushed through the hash.
                    if (fileName is not null) manifest.FileHashes[fileName] = hashingStream.GetHashHex();
                    await hashingStream.DisposeAsync();
                }
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

    // Enumerate a change-logged table's live rows that changed since the watermark, via the change-log:
    // read its Op="U" entries (PK = logical table name, RK = "{pk}|{rk}") and point-read each row. A row
    // deleted after its upsert 404s and is skipped — that delete is recorded by the tombstone pass.
    private static async IAsyncEnumerable<TableEntity> ReadUpsertsViaChangeLogAsync(
        TableClient changeLog, TableClient dataTable, string tableName, DateTimeOffset watermark,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = $"PartitionKey eq '{tableName}' and Op eq 'U' and Timestamp gt datetime'{watermark:O}'";
        await foreach (var logRow in changeLog.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
        {
            // Recover the original key from the authoritative OrigPK/OrigRK columns. A '|' in the PK (sandbox
            // {env}| prefix, legacy {clientId}|{externalId} / {provider}|{providerKey}) makes splitting the
            // composite RK ambiguous — point-reading the wrong key 404s and silently drops the row. Fall back
            // to the split only for legacy change-log rows written before those columns existed.
            var pk = logRow.GetString("OrigPK");
            var rk = logRow.GetString("OrigRK");
            if (pk is null || rk is null)
            {
                var composite = logRow.RowKey;
                var pipe = composite.IndexOf('|');
                if (pipe < 0) continue;
                pk = composite[..pipe];
                rk = composite[(pipe + 1)..];
            }

            TableEntity? live = null;
            try
            {
                live = (await dataTable.GetEntityAsync<TableEntity>(pk, rk, cancellationToken: ct)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Deleted (or table gone) since the upsert was logged; the tombstone pass records the delete.
            }
            if (live is not null) yield return live;
        }
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
                // Upsert entries share this change-log table (Op="U") but are not deletes — the data-table
                // scan captures upserts. Only Op="D" (and legacy rows written before the Op column) belong
                // in the tombstone file; emitting a "U" here would delete a live row on restore.
                if (entity.GetString("Op") == "U") continue;

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
                    // Tombstone format: Table (from PK), PK/RK (from the authoritative OrigPK/OrigRK columns,
                    // with the RK split as a legacy fallback — see ReadUpsertsViaChangeLogAsync for why the
                    // columns are authoritative). A mis-split here removes the wrong key on restore.
                    var origPk = entity.GetString("OrigPK");
                    var origRk = entity.GetString("OrigRK");
                    if (origPk is null || origRk is null)
                    {
                        var rk = entity.RowKey;
                        var pipeIndex = rk.IndexOf('|');
                        origPk = pipeIndex >= 0 ? rk[..pipeIndex] : rk;
                        origRk = pipeIndex >= 0 ? rk[(pipeIndex + 1)..] : "";
                    }
                    var tombstone = new Dictionary<string, object?>
                    {
                        ["Table"] = entity.PartitionKey,
                        ["PartitionKey"] = origPk,
                        ["RowKey"] = origRk,
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

    /// <summary>
    /// Write-only pass-through that SHA-256-hashes everything written to the inner stream. Does not
    /// own the inner stream (the caller disposes it), so it can sit between the gzip/writer chain and
    /// the backup target while the target stream is disposed separately.
    /// </summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
