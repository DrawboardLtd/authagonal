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
        // since the stored (per-run) watermark. All Timestamp filters below use the margin-adjusted
        // effectiveWatermark (see BackupDefaults.WatermarkSkewMargin) — the raw watermark is pod-clock,
        // row Timestamps are storage-clock, and a commit inside the skew would escape every future run.
        // A table the restore path will refuse is not a table worth backing up.
        //
        // Backup accepted any --tables value and wrote, hashed and manifest-signed the result, while
        // RestoreService's allowlist rejected it — and it throws before the --tables filter, mid-loop. So
        // `--tables Users,Clients,RevokedTokens` produced a complete, signed archive that could never be
        // restored, and the failure surfaced during the restore, which is the worst possible moment to
        // discover it. Refused here instead, where the operator is standing at the terminal.
        //
        // Checked against the HOST's declared universe, not this library's own set: a host that keeps its own
        // tables beside Authagonal's backs the two up as one archive, and BackupDefaults.Tables is not a
        // statement about that deployment. See BackupOptions.KnownTables.
        if (options.Tables is { Length: > 0 })
        {
            var known = options.KnownTables ?? BackupDefaults.Tables;
            var unknown = options.Tables
                .Where(t => !known.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"Not part of the backup set: {string.Join(", ", unknown)}. An archive naming a table "
                    + "outside the declared table set is refused by restore, so backing it up would produce "
                    + "an archive that cannot be restored. Transient tables (revoked-token entries, "
                    + "rate-limit counters) are excluded deliberately — they expire on their own and "
                    + "restoring stale rows achieves nothing. A host with tables of its own declares them in "
                    + "BackupOptions.KnownTables, and must pass the same set to RestoreOptions.KnownTables.");
        }

        DateTimeOffset? watermark = null;
        if (options.Incremental)
        {
            watermark = options.WatermarkOverride
                ?? await target.GetLastWatermarkAsync(ct, options.WatermarkScope());
        }
        var effectiveWatermark = watermark - options.WatermarkSkewMargin;

        var backupStart = DateTimeOffset.UtcNow;
        var suffix = watermark.HasValue ? "-incr" : "";
        var backupId = backupStart.ToString("yyyyMMdd-HHmmss") + suffix;

        // One content key per backup, wrapped under the host's KEK. The KEK never touches the data,
        // so it is not applied to gigabytes of plaintext, and a single archive can be released to
        // someone by handing over its wrapped key rather than the KEK itself.
        byte[]? contentKey = null;
        string? wrappedContentKey = null;
        if (options.EncryptionKey is { Length: > 0 } kek)
        {
            contentKey = BackupEncryption.NewContentKey();
            wrappedContentKey = BackupEncryption.WrapKey(contentKey, kek);
        }

        var sw = Stopwatch.StartNew();
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            BackupTimestamp = backupStart,
            Mode = watermark.HasValue ? "incremental" : "full",
            Compressed = options.Gzip,
            Watermark = watermark,
            WrappedContentKey = wrappedContentKey,
            // Which full this incremental applies onto. The field has always been serialized, covered by
            // the manifest MAC and read on the recovery path — and written by nothing, so it was always
            // null and the refusal message that exists to name the chain root named ''.
            //
            // Read from the same scope the watermark came from, so the parent is the full that established
            // this chain and not another prefix's. Null stays possible and is handled: a target that has
            // only ever taken incrementals, or one written before the chain root was recorded, has no full
            // to name.
            ParentBackupId = watermark.HasValue
                ? await target.GetLastFullBackupIdAsync(ct, options.WatermarkScope())
                : null,
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
            Stream? encryptingStream = null;

            try
            {
                // Incremental read for a change-logged table: enumerate its Op="U" change-log entries since
                // the watermark and point-read each live row, rather than scanning the whole table on the
                // unindexed Timestamp column. Full backups (and tables still on the scan path) scan. Deletes
                // are captured separately by the tombstone pass below.
                IAsyncEnumerable<TableEntity> pages;
                if (effectiveWatermark.HasValue && changeLogged.Contains(tableName))
                {
                    (manifest.ChangeLogTables ??= []).Add(tableName);
                    pages = ReadUpsertsViaChangeLogAsync(changeLogClient, tableClient, tableName, effectiveWatermark.Value, ct);
                }
                else
                {
                    var filter = effectiveWatermark.HasValue ? $"Timestamp gt datetime'{effectiveWatermark.Value:O}'" : null;
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
                        // Outermost, so the hash covers the CIPHERTEXT — which is what restore reads
                        // and can check before it holds a key, rather than after.
                        hashingStream = new HashingStream(outputStream);
                        // leaveOpen: true on both layers — otherwise disposing the writer cascades
                        // (writer → gzip → hashingStream → IncrementalHash), disposing the hash BEFORE
                        // GetHashHex() reads it below, which throws ObjectDisposedException. We dispose
                        // hashingStream explicitly after reading the hash.
                        // Compress THEN encrypt: the other order would compress ciphertext, which does
                        // not compress, and would leak nothing useful in exchange.
                        Stream sink = hashingStream;
                        if (contentKey is not null)
                        {
                            encryptingStream = BackupEncryption.Encrypt(hashingStream, contentKey, fileName, leaveOpen: true);
                            sink = encryptingStream;
                        }

                        if (options.Gzip)
                        {
                            gzipStream = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true);
                            writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
                        }
                        else
                        {
                            writer = new StreamWriter(sink, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
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
                // Disposed BEFORE the hash is read: this is what writes the terminator frame, and
                // those bytes have to be inside the digest.
                if (encryptingStream is not null) await encryptingStream.DisposeAsync();
                if (hashingStream is not null)
                {
                    // writer + gzip + encryption are disposed, so every byte has flushed through the hash.
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
        if (effectiveWatermark.HasValue)
        {
            tombstoneCount = await BackupTombstonesAsync(backupId, prefix, effectiveWatermark.Value, manifest, ct);
        }

        sw.Stop();
        manifest.TombstoneCount = tombstoneCount;
        manifest.TotalEntities = totalEntities;
        manifest.DurationSeconds = sw.Elapsed.TotalSeconds;

        if (!options.DryRun)
        {
            // Signed last, once every file hash is in. Without a MAC the manifest authenticates
            // nothing — it sits beside the files it vouches for, on the same target.
            if (options.ManifestKey is { Length: > 0 } manifestKey)
                ManifestAuthentication.Sign(manifest, manifestKey);

            await target.WriteManifestAsync(backupId, manifest, ct);
            await target.SetLastWatermarkAsync(backupStart, ct, options.WatermarkScope());

            // A completed full backup becomes the chain root every later incremental in this scope names.
            // After the manifest, so a run that failed to write its manifest does not become the parent of
            // archives that cannot be restored from it.
            if (!watermark.HasValue)
                await target.SetLastFullBackupIdAsync(backupId, ct, options.WatermarkScope());
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

    private async Task<long> BackupTombstonesAsync(
        string backupId, string prefix, DateTimeOffset watermark, BackupManifest manifest, CancellationToken ct)
    {
        HashingStream? hashingStream = null;
        string? tombstoneFileName = null;
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
                    tombstoneFileName = $"_tombstones{ext}";
                    outputStream = await target.OpenWriteAsync(backupId, tombstoneFileName, ct);

                    // Hashed like every data file. It was the one backup artefact written with no
                    // HashingStream and never entered into manifest.FileHashes — so restore had
                    // nothing to verify it against and applied its deletes unchecked. A tombstone
                    // file is a list of rows to remove, which makes an unverified one a way to
                    // delete attacker-chosen records (a revocation entry, say) during a restore.
                    hashingStream = new HashingStream(outputStream);
                    // leaveOpen: true on both layers, for the reason spelled out on the data-file
                    // path above — otherwise disposing the writer cascades down to the
                    // IncrementalHash and GetHashHex() throws ObjectDisposedException.
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
                    // DeletedAt drives MergeService's recreate check against captured rows' STORAGE-clock
                    // Timestamps, so emit the change-log row's own storage Timestamp (same clock domain;
                    // the row is upsert-replaced per key, so its Timestamp IS the delete time). The stored
                    // DeletedAt column is pod-clock — comparing it to a storage Timestamp let a
                    // delete-then-recreate within the skew drop the recreated row from rollups (F24f).
                    var tombstone = new Dictionary<string, object?>
                    {
                        ["Table"] = entity.PartitionKey,
                        ["PartitionKey"] = origPk,
                        ["RowKey"] = origRk,
                        ["DeletedAt"] = entity.Timestamp ?? entity.GetDateTimeOffset("DeletedAt"),
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

            // Recorded before the underlying stream closes, so the digest covers everything written.
            if (hashingStream is not null && tombstoneFileName is not null)
                manifest.FileHashes[tombstoneFileName] = hashingStream.GetHashHex();

            if (hashingStream is not null) await hashingStream.DisposeAsync();
            if (outputStream is not null) await outputStream.DisposeAsync();
        }

        return count;
    }

    internal static Dictionary<string, object?> SerializeEntity(TableEntity entity)
    {
        var dict = new Dictionary<string, object?>
        {
            // Format marker: rows carrying it store explicit EDM type annotations for every
            // JSON-ambiguous column, so restore never re-infers a type. Without it (legacy rows),
            // restore falls back to shape-based inference — which mis-typed GUID/date-SHAPED
            // string columns (e.g. the index tables' string UserId) as Edm.Guid/Edm.DateTime,
            // breaking typed reads after a restore.
            ["@v"] = 2,
            ["PartitionKey"] = entity.PartitionKey,
            ["RowKey"] = entity.RowKey,
            ["Timestamp"] = entity.Timestamp,
            ["ETag"] = entity.ETag.ToString(),
        };

        foreach (var kvp in entity)
        {
            if (kvp.Key is "odata.etag") continue;
            if (!dict.TryAdd(kvp.Key, kvp.Value)) continue;

            // Mirror the wire protocol's odata type annotations for the types JSON can't represent
            // unambiguously. Strings/ints/bools need none — an unannotated value restores as-is.
            var edmType = kvp.Value switch
            {
                Guid => "Edm.Guid",
                DateTimeOffset or DateTime => "Edm.DateTime",
                byte[] or BinaryData => "Edm.Binary",
                long => "Edm.Int64",
                double => "Edm.Double",
                _ => null,
            };
            if (edmType is not null) dict[$"{kvp.Key}@odata.type"] = edmType;
        }

        return dict;
    }


}
