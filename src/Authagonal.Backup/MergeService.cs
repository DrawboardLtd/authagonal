using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Authagonal.Backup;

/// <summary>
/// Merges a full backup with incremental backups and tombstones into a single current-state view.
/// Processes one table at a time, and STREAMS the full backup through — only the incremental
/// overlay and the tombstone set are held in memory (both bounded by the change window), so a
/// large tenant's rollup can't balloon the host's memory the way the old whole-table dictionary did.
/// </summary>
public sealed class MergeService(IBackupSource source)
{
    /// <summary>
    /// Merges a full backup and incrementals into a new full backup written to the target.
    /// <paramref name="newBackupId"/> names the merged backup; null derives a plain timestamp id.
    /// A caller producing a specially-retained snapshot (e.g. a "-weekly") MUST pass the id here —
    /// tagging only the manifest leaves the physical id untagged, so id-based retention/selection
    /// (which lists blob prefixes, not manifests) still treats it as an ordinary daily full.
    /// </summary>
    /// <param name="encryptionKey">
    /// The key-encryption key the SOURCE backups were written with, and the one the merged output is
    /// written with.
    /// </param>
    /// <remarks>
    /// Required when any input is encrypted: without it the merge cannot read them, and — worse — a
    /// rollup that silently produced a PLAINTEXT snapshot from encrypted inputs would be a downgrade
    /// performed by the retention job itself, on the copy that outlives everything else.
    /// </remarks>
    /// <param name="manifestKey">
    /// The HMAC key the source backups' manifests were signed with, and the one the merged manifest is signed
    /// with. Supplying it also turns on verification of every input file against its own manifest's hashes.
    /// </param>
    /// <remarks>
    /// Two halves, and they belong together. The merged manifest previously carried no MAC at all, so a
    /// rolled-up snapshot was unrestorable without <c>AllowUnauthenticatedManifest</c> — and since
    /// <c>RollupAndCleanAsync</c> then DELETES the signed full and incrementals, the retention path turned
    /// every authenticated backup into an unauthenticated one and the operator's only way back was to accept
    /// hashes sitting in a plain JSON file beside the data on the same target. That is precisely the
    /// circularity <c>ManifestMac</c> exists to remove.
    /// <para>
    /// Signing the output alone would have been worse than useless: the merge verified NOTHING about what it
    /// read, and the output is hashed fresh, so the new manifest vouches for whatever the merge happened to
    /// find. An attacker with write access to the target edits <c>Users.jsonl</c> in yesterday's full — a
    /// password hash, say — leaves the manifest alone, and tonight's rollup writes the tampered bytes into a
    /// new snapshot with correct hashes and a valid MAC over them, then deletes the original. The tamper
    /// becomes indistinguishable from legitimate content, authenticated by the deployment's own key.
    /// </para>
    /// </remarks>
    public async Task<BackupManifest> MergeToTargetAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        IBackupTarget target,
        bool gzip = true,
        CancellationToken ct = default,
        string? newBackupId = null,
        byte[]? encryptionKey = null,
        byte[]? manifestKey = null)
    {
        // Content keys are per backup, so each input has its own — resolved once here rather than per
        // file, and the merged output gets a fresh one of its own.
        _sourceContentKeys.Clear();
        _sourceManifests.Clear();
        foreach (var id in incrementalBackupIds.Prepend(fullBackupId))
        {
            var sourceManifest = await source.ReadManifestAsync(id, ct);

            // Every input's manifest is kept, whether or not a manifestKey was supplied, because the FILE
            // HASHES it records are useful on their own: there is no reason to read a file unverified when its
            // own manifest says what it should hash to. Deployments that authenticate manifests out of band —
            // the cloud host signs `_manifest.sig` with Vault Transit rather than an HMAC key here — get the
            // per-file check with no caller change, which matters because that host recomputes the hashes from
            // whatever is on the target and then signs THOSE. Without verifying the inputs, its signature
            // attests to the tamper.
            if (sourceManifest is not null)
                _sourceManifests[id] = sourceManifest;

            // The MAC is the separate question — is the manifest itself authentic — and only a key answers it.
            // Refusing rather than warning is the point: the merged output is the copy that outlives its
            // sources, so this is the last moment at which anyone can still tell.
            if (manifestKey is { Length: > 0 })
            {
                if (sourceManifest is null)
                    throw new InvalidOperationException(
                        $"Backup '{id}' has no manifest, so the rollup cannot authenticate what it reads. " +
                        "Roll up without a manifestKey to accept unverified inputs.");

                if (!ManifestAuthentication.Verify(sourceManifest, manifestKey))
                    throw new InvalidOperationException(
                        $"Backup '{id}' manifest failed authentication, so the rollup would launder it into a " +
                        "freshly hashed and signed snapshot. Refusing to merge it.");
            }

            if (string.IsNullOrEmpty(sourceManifest?.WrappedContentKey)) continue;

            if (encryptionKey is not { Length: > 0 })
                throw new InvalidOperationException(
                    $"Backup '{id}' is encrypted but no encryptionKey was supplied to the rollup. " +
                    "Merging without it would either fail to read the inputs or write a plaintext " +
                    "snapshot from encrypted ones.");

            _sourceContentKeys[id] = BackupEncryption.UnwrapKey(sourceManifest.WrappedContentKey, encryptionKey);
        }

        byte[]? outputContentKey = null;
        string? wrappedOutputKey = null;
        if (encryptionKey is { Length: > 0 })
        {
            outputContentKey = BackupEncryption.NewContentKey();
            wrappedOutputKey = BackupEncryption.WrapKey(outputContentKey, encryptionKey);
        }

        var allTables = await CollectTableNamesAsync(fullBackupId, incrementalBackupIds, ct);
        var tombstones = await LoadTombstonesAsync(incrementalBackupIds, ct);

        var backupStart = DateTimeOffset.UtcNow;
        var backupId = newBackupId ?? backupStart.ToString("yyyyMMdd-HHmmss");
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            BackupTimestamp = backupStart,
            Mode = "full",
            Compressed = gzip,
            WrappedContentKey = wrappedOutputKey,
        };

        long totalEntities = 0;

        foreach (var tableName in allTables)
        {
            tombstones.TryGetValue(tableName, out var tableTombstones);

            // Output opens lazily on the first surviving row, so an empty table writes no file —
            // same behaviour as the old dictionary merge.
            StreamWriter? writer = null;
            GZipStream? gzipStream = null;
            Stream? encryptingStream = null;
            HashingStream? hashingStream = null;
            Stream? outputStream = null;
            string? fileName = null;

            // Hashed on the way out, exactly as BackupService does.
            //
            // This reimplemented the write path with a plain GZipStream/StreamWriter and computed no
            // hash, so every merged manifest carried an empty FileHashes — and RestoreService gates
            // the whole verification step on that dictionary being non-empty. So a rolled-up backup
            // restored entirely unverified, and the only signal was a line on Console.Error from
            // inside a library. Rollups are the documented long-term-retention path, and
            // RollupAndCleanAsync then DELETES the hashed full and incrementals: after a rollup the
            // only surviving copy of the data was the one that could not be checked.
            var count = await MergeTableAsync(
                fullBackupId, incrementalBackupIds, tableName, tableTombstones,
                openWriter: async () =>
                {
                    var ext = gzip ? ".jsonl.gz" : ".jsonl";
                    fileName = $"{tableName}{ext}";
                    outputStream = await target.OpenWriteAsync(backupId, fileName, ct);
                    hashingStream = new HashingStream(outputStream);
                    Stream sink = hashingStream;
                    if (outputContentKey is not null)
                    {
                        encryptingStream = BackupEncryption.Encrypt(hashingStream, outputContentKey, fileName, leaveOpen: true);
                        sink = encryptingStream;
                    }
                    // leaveOpen on both layers so disposing the writer does not cascade into the hash
                    // before GetHashHex reads it — same reason, and the same shape, as BackupService.
                    if (gzip)
                    {
                        gzipStream = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true);
                        writer = new StreamWriter(gzipStream, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
                    }
                    else
                    {
                        writer = new StreamWriter(sink, System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
                    }
                    return writer;
                }, ct);

            if (writer is not null) await writer.DisposeAsync();
            if (gzipStream is not null) await gzipStream.DisposeAsync();
            // Before the hash is read: this writes the terminator frame.
            if (encryptingStream is not null) await encryptingStream.DisposeAsync();
            if (hashingStream is not null)
            {
                // writer + gzip are disposed, so every byte has flushed through the hash.
                if (fileName is not null) manifest.FileHashes[fileName] = hashingStream.GetHashHex();
                await hashingStream.DisposeAsync();
            }
            if (outputStream is not null) await outputStream.DisposeAsync();

            if (count == 0) continue;
            manifest.Tables[tableName] = new TableBackupInfo
            {
                EntityCount = count,
            };
            totalEntities += count;
        }

        manifest.TotalEntities = totalEntities;
        manifest.DurationSeconds = (DateTimeOffset.UtcNow - backupStart).TotalSeconds;

        // Last, so the MAC covers the completed manifest including every file hash — same order as
        // BackupService. Without this the retained snapshot was the one copy that could not be authenticated.
        if (manifestKey is { Length: > 0 })
            ManifestAuthentication.Sign(manifest, manifestKey);

        await target.WriteManifestAsync(backupId, manifest, ct);

        return manifest;
    }

    /// <summary>
    /// Merges full + incrementals and streams the result to a callback as a JSONL stream per table.
    /// The callback is invoked (lazily — only for tables with at least one surviving row) with the
    /// table name and a live read stream; it MUST consume the stream to completion.
    /// </summary>
    public async Task MergeToCallbackAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        Func<string, Stream, Task> onTable,
        CancellationToken ct = default)
    {
        var allTables = await CollectTableNamesAsync(fullBackupId, incrementalBackupIds, ct);
        var tombstones = await LoadTombstonesAsync(incrementalBackupIds, ct);

        foreach (var tableName in allTables)
        {
            tombstones.TryGetValue(tableName, out var tableTombstones);

            // The consumer reads a Pipe the merge writes into — nothing is buffered beyond the pipe's
            // window. The consumer task starts on the first surviving row (empty tables never invoke
            // the callback, matching the old behaviour).
            Pipe? pipe = null;
            Task? consumer = null;
            StreamWriter? writer = null;
            try
            {
                await MergeTableAsync(
                    fullBackupId, incrementalBackupIds, tableName, tableTombstones,
                    openWriter: () =>
                    {
                        pipe = new Pipe();
                        consumer = Task.Run(() => onTable(tableName, pipe.Reader.AsStream()), ct);
                        writer = new StreamWriter(pipe.Writer.AsStream(), System.Text.Encoding.UTF8);
                        return Task.FromResult(writer);
                    }, ct);

                if (writer is not null)
                    await writer.DisposeAsync(); // flush + complete the pipe writer → consumer sees EOF
            }
            catch (Exception ex)
            {
                // Fault the pipe so the consumer unblocks, then swallow its secondary failure —
                // the merge exception is the one worth surfacing.
                if (pipe is not null) await pipe.Writer.CompleteAsync(ex);
                if (consumer is not null) { try { await consumer; } catch { /* secondary */ } }
                throw;
            }

            if (consumer is not null)
                await consumer;
        }
    }

    /// <summary>Union of table names across the full and every incremental manifest.</summary>
    private async Task<HashSet<string>> CollectTableNamesAsync(
        string fullBackupId, IReadOnlyList<string> incrementalBackupIds, CancellationToken ct)
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
        return allTables;
    }

    /// <summary>
    /// The streaming merge core for one table. The incrementals fold into an in-memory overlay
    /// (later incrementals win); the full then streams through line by line — a row superseded by
    /// the overlay or deleted by a tombstone is dropped, everything else passes straight to the
    /// writer. Overlay rows (minus tombstoned ones) are appended last. Row order is not part of the
    /// backup contract (restore upserts row-by-row). Returns rows written; the writer factory is
    /// only invoked when there is at least one row to write.
    /// </summary>
    private async Task<int> MergeTableAsync(
        string fullBackupId,
        IReadOnlyList<string> incrementalBackupIds,
        string tableName,
        Dictionary<(string PK, string RK), DateTimeOffset?>? tableTombstones,
        Func<Task<StreamWriter>> openWriter,
        CancellationToken ct)
    {
        var overlay = new Dictionary<(string PK, string RK), string>();
        foreach (var incrId in incrementalBackupIds)
        {
            await foreach (var (key, line) in ReadEntitiesAsync(incrId, tableName, ct))
                overlay[key] = line;
        }

        StreamWriter? writer = null;
        var written = 0;

        await foreach (var (key, line) in ReadEntitiesAsync(fullBackupId, tableName, ct))
        {
            if (overlay.ContainsKey(key)) continue;                    // superseded by an incremental
            if (IsDeleted(key, line, tableTombstones)) continue;       // tombstoned, not recreated
            writer ??= await openWriter();
            await writer.WriteLineAsync(line.AsMemory(), ct);
            written++;
        }

        foreach (var (key, line) in overlay)
        {
            if (IsDeleted(key, line, tableTombstones)) continue;
            writer ??= await openWriter();
            await writer.WriteLineAsync(line.AsMemory(), ct);
            written++;
        }

        if (writer is not null)
            await writer.FlushAsync(ct);
        return written;
    }

    // Apply a delete only when it postdates the captured row. Incrementals are pooled and deletes
    // applied after all upserts, so a key deleted early in the window and recreated later has both a
    // tombstone and a live capture — an unconditional remove would drop the recreated row from the
    // merged full. Equal timestamps remove (a row can't be deleted before it was written). A tombstone
    // without DeletedAt (legacy) or a row without a parseable Timestamp falls back to removing.
    private static bool IsDeleted(
        (string PK, string RK) key, string line,
        Dictionary<(string PK, string RK), DateTimeOffset?>? tableTombstones)
    {
        if (tableTombstones is null || !tableTombstones.TryGetValue(key, out var deletedAt))
            return false;
        if (deletedAt is null)
            return true;

        using var doc = JsonDocument.Parse(line);
        var recreatedAfterDelete =
            doc.RootElement.TryGetProperty("Timestamp", out var tsProp) &&
            tsProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(tsProp.GetString(), out var ts) &&
            ts > deletedAt.Value;

        return !recreatedAfterDelete;
    }

    /// <summary>Streams a backed-up table's rows as ((PK, RK), jsonLine) without materializing the
    /// table. Auto-detects gzip; yields nothing when the file is absent.</summary>
    /// <summary>Unwrapped content key per source backup id, for the current merge.</summary>
    private readonly Dictionary<string, byte[]> _sourceContentKeys = new(StringComparer.Ordinal);

    /// <summary>Manifest per source backup id, for the current merge.</summary>
    private readonly Dictionary<string, BackupManifest> _sourceManifests = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a source file, verified against the hash its OWN backup's manifest records for it.
    /// </summary>
    /// <remarks>
    /// The merge previously read every input with a bare <c>OpenReadAsync</c> and consulted
    /// <c>FileHashes</c> for none of them, while hashing its output fresh — so the new manifest vouched for
    /// whatever the merge happened to find, and the retention job laundered a tampered archive into an
    /// authenticated one.
    /// <para>
    /// A file the manifest LISTS but the store does not hold is an error rather than an empty read. Without
    /// that, deleting a data file from an input silently dropped every row in it: the merge contributed nothing
    /// for that table and the output recorded the smaller, hashed, signed result as correct — a way to remove a
    /// whole table's worth of records that survives every integrity check downstream. This is the gap the
    /// restore path already closes for its own inputs.
    /// </para>
    /// <para>
    /// Verified BEFORE decryption, so the hash covers the bytes on disk — the same order as the restore path,
    /// and the order in which they were written.
    /// </para>
    /// </remarks>
    private async Task<Stream?> OpenSourceAsync(string backupId, string fileName, CancellationToken ct)
    {
        string? expectedHash = null;
        var listed = _sourceManifests.TryGetValue(backupId, out var m)
            && m.FileHashes.TryGetValue(fileName, out expectedHash);

        var stream = await VerifiedRead.OpenAsync(source, backupId, fileName, expectedHash, ct);

        if (stream is null && listed)
            throw new InvalidOperationException(
                $"Backup '{backupId}' manifest lists '{fileName}' but the store does not hold it. " +
                "Refusing to roll up a snapshot with a missing file: the merged output would silently omit " +
                "its rows and then vouch for the result.");

        return stream;
    }

    private async IAsyncEnumerable<((string PK, string RK) Key, string Line)> ReadEntitiesAsync(
        string backupId, string tableName, [EnumeratorCancellation] CancellationToken ct)
    {
        // The ACTUAL file name matters: it is bound into the encryption's associated data, so guessing
        // the compressed variant when the uncompressed one is on disk fails authentication.
        var fileName = $"{tableName}.jsonl.gz";
        var stream = await OpenSourceAsync(backupId, fileName, ct);
        if (stream is null)
        {
            fileName = $"{tableName}.jsonl";
            stream = await OpenSourceAsync(backupId, fileName, ct);
        }

        if (stream is null) yield break;

        await using (stream)
        {
            var encrypted = _sourceContentKeys.TryGetValue(backupId, out var key);
            var plain = encrypted ? BackupEncryption.Decrypt(stream, key!, fileName) : stream;
            await using var decryptScope = ReferenceEquals(plain, stream) ? null : plain;

            // The gzip sniff rewinds, which a decrypting stream cannot do — and does not need to:
            // an encrypted file was written by this code, so its extension is authoritative. The
            // sniff stays for plaintext archives, where it has always covered a mislabelled file.
            Stream readStream;
            Stream? sniffScope = null;
            if (encrypted)
            {
                readStream = fileName.EndsWith(".gz", StringComparison.Ordinal)
                    ? new GZipStream(plain, CompressionMode.Decompress)
                    : plain;
            }
            else
            {
                var buffered = new BufferedStream(plain);
                sniffScope = buffered;
                var header = new byte[2];
                var read = await buffered.ReadAsync(header, ct);
                buffered.Position = 0;
                readStream = read >= 2 && header[0] == 0x1f && header[1] == 0x8b
                    ? new GZipStream(buffered, CompressionMode.Decompress)
                    : buffered;
            }

            await using var decompressScope = ReferenceEquals(readStream, plain) || ReferenceEquals(readStream, sniffScope)
                ? null
                : readStream;
            using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string pk, rk;
                using (var doc = JsonDocument.Parse(line))
                {
                    pk = doc.RootElement.GetProperty("PartitionKey").GetString()!;
                    rk = doc.RootElement.GetProperty("RowKey").GetString()!;
                }
                yield return ((pk, rk), line);
            }
        }
    }

    private async Task<Dictionary<string, Dictionary<(string PK, string RK), DateTimeOffset?>>> LoadTombstonesAsync(
        IReadOnlyList<string> incrementalBackupIds, CancellationToken ct)
    {
        var tombstones = new Dictionary<string, Dictionary<(string, string), DateTimeOffset?>>();

        foreach (var incrId in incrementalBackupIds)
        {
            // Verified like a data file. Its content is a list of rows to DELETE from the merged output, so a
            // tampered one removes records of the attacker's choosing from the copy that outlives the sources.
            var tombstoneFile = "_tombstones.jsonl.gz";
            var stream = await OpenSourceAsync(incrId, tombstoneFile, ct);
            if (stream is null)
            {
                tombstoneFile = "_tombstones.jsonl";
                stream = await OpenSourceAsync(incrId, tombstoneFile, ct);
            }

            if (stream is null) continue;

            await using (stream)
            {
                var encrypted = _sourceContentKeys.TryGetValue(incrId, out var key);
                var plain = encrypted ? BackupEncryption.Decrypt(stream, key!, tombstoneFile) : stream;
                await using var decryptScope = ReferenceEquals(plain, stream) ? null : plain;

                Stream readStream;
                Stream? sniffScope = null;
                if (encrypted)
                {
                    readStream = tombstoneFile.EndsWith(".gz", StringComparison.Ordinal)
                        ? new GZipStream(plain, CompressionMode.Decompress)
                        : plain;
                }
                else
                {
                    var buffered = new BufferedStream(plain);
                    sniffScope = buffered;
                    var header = new byte[2];
                    var read = await buffered.ReadAsync(header, ct);
                    buffered.Position = 0;
                    readStream = read >= 2 && header[0] == 0x1f && header[1] == 0x8b
                        ? new GZipStream(buffered, CompressionMode.Decompress)
                        : buffered;
                }

                await using var decompressScope = ReferenceEquals(readStream, plain) || ReferenceEquals(readStream, sniffScope)
                    ? null
                    : readStream;
                using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    using var doc = JsonDocument.Parse(line);
                    var table = doc.RootElement.GetProperty("Table").GetString()!;
                    var pk = doc.RootElement.GetProperty("PartitionKey").GetString()!;
                    var rk = doc.RootElement.GetProperty("RowKey").GetString()!;
                    DateTimeOffset? deletedAt = null;
                    if (doc.RootElement.TryGetProperty("DeletedAt", out var deletedAtProp) &&
                        deletedAtProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(deletedAtProp.GetString(), out var parsed))
                    {
                        deletedAt = parsed;
                    }

                    if (!tombstones.TryGetValue(table, out var set))
                    {
                        set = new Dictionary<(string, string), DateTimeOffset?>();
                        tombstones[table] = set;
                    }
                    // Keep the LATEST delete per key; a null DeletedAt (legacy tombstone) means
                    // "unconditionally remove", so it wins over any timestamped delete.
                    if (!set.TryGetValue((pk, rk), out var existing) ||
                        existing is not null && (deletedAt is null || deletedAt > existing))
                    {
                        set[(pk, rk)] = deletedAt;
                    }
                }
            }
        }

        return tombstones;
    }
}
