using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Backup;

public sealed class RestoreService(TableServiceClient serviceClient, IBackupSource source, RestoreOptions options)
{
    public async Task<RestoreResult> RunAsync(string backupId, CancellationToken ct = default)
    {
        var prefix = options.TablePrefix ?? "";
        var files = await source.ListFilesAsync(backupId, ct);
        var result = new RestoreResult();

        var manifest = await source.ReadManifestAsync(backupId, ct);
        var canVerify = options.VerifyIntegrity && manifest?.FileHashes is { Count: > 0 };
        if (options.VerifyIntegrity && !canVerify && !options.AllowUnverified)
        {
            // Refused, not warned. This used to print to Console.Error and restore anyway — which
            // inside a host process is a signal nobody sees, and it produced a restore that reported
            // success having checked nothing. Every backup this code writes now carries hashes
            // (merged ones included, which is what made this reachable at all), so reaching here
            // means either a backup older than integrity hashing or a manifest that has lost them.
            throw new InvalidOperationException(
                "Backup has no recorded file hashes, so integrity cannot be verified. Restore with " +
                "VerifyIntegrity disabled, or set AllowUnverified if this is a backup taken before " +
                "integrity hashing existed and unverified data is acceptable.");
        }

        // Authenticate the manifest itself before trusting the hashes in it. Verifying files against
        // a manifest that sits unsigned beside them on the same target detects corruption but not
        // tampering: whoever can rewrite a data file can rewrite its recorded hash too. The key comes
        // from outside the target, so an attacker holding only the backup cannot forge the MAC.
        if (options.ManifestKey is { Length: > 0 } manifestKey)
        {
            if (manifest is null || !ManifestAuthentication.Verify(manifest, manifestKey))
            {
                throw new InvalidOperationException(
                    "Backup manifest failed authentication: its MAC is missing or does not verify under " +
                    "the supplied key. The file hashes it carries cannot be trusted, so neither can the " +
                    "backup. Restore without ManifestKey only if you accept unauthenticated data.");
            }
        }
        else if (options.VerifyIntegrity)
        {
            Console.Error.WriteLine(
                "WARNING: no ManifestKey supplied, so the manifest is unauthenticated. File hashes " +
                "detect corruption but not tampering — anyone who can rewrite a backup file can rewrite " +
                "its recorded hash.");
        }

        if (options.Mode == RestoreMode.Clean)
        {
            // The manifest has always recorded whether this backup is a full or an incremental; nothing
            // used it. Cleaning before applying an incremental empties the table and then writes back only
            // the changed rows, so every row that did NOT change in the window is destroyed.
            var isIncremental = string.Equals(manifest?.Mode, "incremental", StringComparison.OrdinalIgnoreCase);
            if (isIncremental && !options.AllowCleanFromIncremental)
                throw new InvalidOperationException(
                    $"Refusing a Clean restore of incremental backup '{backupId}': it contains only rows changed " +
                    $"since {manifest?.Watermark:o}, so cleaning first would destroy every unchanged row. Restore the " +
                    $"parent full ('{manifest?.ParentBackupId}') with --mode clean, then this incremental with " +
                    "--mode upsert. Pass AllowCleanFromIncremental to override.");

            if (options.CleanEnvPrefix is null && !options.DryRun)
                Console.Error.WriteLine(
                    "WARNING: --mode clean will empty the ENTIRE target table(s). If these tables hold more than " +
                    "one env, set CleanEnvPrefix to scope the wipe to a single env.");
        }

        foreach (var fileName in files)
        {
            if (fileName.StartsWith("_")) continue; // Skip metadata files (manifest, tombstones)

            var tableName = ExtractTableName(fileName);
            if (tableName is null) continue;

            if (options.Tables is not null && !options.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                continue;

            // Verify the entire file against the manifest BEFORE applying any of its entities, so a
            // tampered backup can't inject an admin client, reset a password hash, or plant a
            // signing key. A file absent from the manifest is treated as tampering.
            if (canVerify)
            {
                if (!manifest!.FileHashes.TryGetValue(fileName, out var expectedHash))
                    throw new InvalidOperationException(
                        $"Backup integrity check failed: '{fileName}' is not listed in the manifest.");
                var actualHash = await ComputeFileHashAsync(backupId, fileName, ct);
                if (actualHash is null || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Backup integrity check failed: '{fileName}' hash does not match the manifest.");
            }

            var physicalName = prefix + tableName;
            var tableClient = serviceClient.GetTableClient(physicalName);
            tableClient.CreateIfNotExists(ct);

            // Gated on DryRun exactly like the entity writes below and the tombstone deletes: a dry run
            // must not mutate anything, and an ungated clean made `--dry-run --mode clean` the single most
            // destructive command in the tool.
            if (options.Mode == RestoreMode.Clean && !options.DryRun)
            {
                await CleanTableAsync(tableClient, options.CleanEnvPrefix, ct);
            }

            var stream = await source.OpenReadAsync(backupId, fileName, ct);
            if (stream is null) continue;

            long restored = 0;
            long errors = 0;

            await using (stream)
            {
                Stream readStream = fileName.EndsWith(".gz") ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                await using var decompressScope = fileName.EndsWith(".gz") ? readStream : null;
                using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var entity = DeserializeEntity(line);
                        if (entity is null) continue;

                        if (!options.DryRun)
                        {
                            var mode = options.Mode == RestoreMode.Merge
                                ? TableUpdateMode.Merge
                                : TableUpdateMode.Replace;
                            await tableClient.UpsertEntityAsync(entity, mode, ct);
                        }

                        restored++;
                    }
                    catch (RequestFailedException)
                    {
                        errors++;
                    }
                }
            }

            result.Tables[tableName] = new RestoreTableResult { Restored = restored, Errors = errors };
        }

        // F24b: honor the backup's deletes. Restoring full + incrementals without applying tombstones
        // resurrects every row deleted in the window (incl. GDPR erasures) — the tombstone file is as
        // much a part of an incremental's state as its data files. Applied AFTER the data files; within
        // one backup a key never has both a live capture and a tombstone (the change-log is upsert-
        // replaced per key, last op wins), and across backups the operator applies them oldest-first, so
        // a later incremental's recreate lands after the earlier delete.
        if (options.ApplyTombstones)
        {
            result.TombstonesApplied = await ApplyTombstonesAsync(backupId, files, prefix, manifest, canVerify, ct);
        }

        return result;
    }

    private async Task<long> ApplyTombstonesAsync(
        string backupId, IReadOnlyList<string> files, string prefix,
        BackupManifest? manifest, bool canVerify, CancellationToken ct)
    {
        var fileName = files.FirstOrDefault(f => f is "_tombstones.jsonl.gz" or "_tombstones.jsonl");
        if (fileName is null) return 0; // full backups (and empty incrementals) carry no tombstone file

        // A tampered tombstone file deletes attacker-chosen rows (e.g. a revocation record), so verify
        // it like a data file when a hash is recorded. Backups written before the tombstone file was
        // hashed can't be verified — warn loudly rather than making them unrestorable.
        if (canVerify)
        {
            if (manifest!.FileHashes.TryGetValue(fileName, out var expectedHash))
            {
                var actualHash = await ComputeFileHashAsync(backupId, fileName, ct);
                if (actualHash is null || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Backup integrity check failed: '{fileName}' hash does not match the manifest.");
            }
            else
            {
                // Fatal, matching the data-file rule. A tombstone file absent from the manifest is
                // exactly as much evidence of tampering as an unlisted data file — and it is the more
                // dangerous of the two, because its content is a list of rows to DELETE. Warning and
                // proceeding meant an attacker who could write to the backup target could have a
                // restore remove records of their choosing.
                throw new InvalidOperationException(
                    $"Backup integrity check failed: '{fileName}' has no hash recorded in the manifest. " +
                    "Pass SkipIntegrityCheck to restore anyway, accepting unverified deletes.");
            }
        }

        var stream = await source.OpenReadAsync(backupId, fileName, ct);
        if (stream is null) return 0;

        long applied = 0;
        await using (stream)
        {
            Stream readStream = fileName.EndsWith(".gz") ? new GZipStream(stream, CompressionMode.Decompress) : stream;
            await using var decompressScope = fileName.EndsWith(".gz") ? readStream : null;
            using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var tableName = root.GetProperty("Table").GetString();
                var pk = root.GetProperty("PartitionKey").GetString();
                var rk = root.GetProperty("RowKey").GetString();
                if (string.IsNullOrEmpty(tableName) || pk is null || rk is null) continue;

                if (options.Tables is not null && !options.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!options.DryRun)
                {
                    try
                    {
                        await serviceClient.GetTableClient(prefix + tableName).DeleteEntityAsync(pk, rk, ETag.All, ct);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }

                applied++;
            }
        }

        return applied;
    }

    private async Task<string?> ComputeFileHashAsync(string backupId, string fileName, CancellationToken ct)
    {
        var stream = await source.OpenReadAsync(backupId, fileName, ct);
        if (stream is null) return null;
        await using (stream)
        {
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hash);
        }
    }

    /// <summary>
    /// Deletes existing rows ahead of a Clean restore. When <paramref name="envPrefix"/> is supplied the
    /// wipe is confined to that env's PartitionKey range, so restoring one sandbox env does not destroy
    /// its siblings sharing the same physical table. Callers must not invoke this on a dry run.
    /// </summary>
    private static async Task CleanTableAsync(TableClient tableClient, string? envPrefix, CancellationToken ct)
    {
        // Range filter rather than startswith: Table Storage has no prefix operator, and a PK range is a
        // partition-ordered scan. The upper bound appends char.MaxValue so keys containing non-ASCII
        // characters are still inside the range (a '~' bound would silently skip them, leaving rows behind
        // that the restore then collides with).
        string? filter = null;
        if (!string.IsNullOrEmpty(envPrefix))
        {
            var lo = envPrefix;
            var hi = envPrefix + char.MaxValue;
            filter = $"PartitionKey ge '{Escape(lo)}' and PartitionKey lt '{Escape(hi)}'";
        }

        var query = tableClient.QueryAsync<TableEntity>(
            filter: filter,
            select: new[] { "PartitionKey", "RowKey" },
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            try
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }

    /// <summary>Escapes a single quote for an OData string literal (doubled, per OData).</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    internal static TableEntity? DeserializeEntity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("PartitionKey", out var pkProp) ||
            !root.TryGetProperty("RowKey", out var rkProp))
            return null;

        var entity = new TableEntity(pkProp.GetString(), rkProp.GetString());

        // Rows written with the "@v" format marker carry an explicit "{col}@odata.type" annotation for
        // every JSON-ambiguous column, so types restore EXACTLY: an unannotated string IS a string.
        // Legacy rows (no marker) fall back to shape-based inference — which is why the marker exists:
        // inference re-typed GUID/date-SHAPED string columns (the index tables' string UserId) as
        // Edm.Guid/Edm.DateTime, and the typed read after a restore then failed to bind (F24a).
        var typed = root.TryGetProperty("@v", out _);

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "PartitionKey" or "RowKey" or "Timestamp" or "ETag" or "odata.etag" or "@v")
                continue;
            if (prop.Name.EndsWith("@odata.type", StringComparison.Ordinal))
                continue;

            if (typed)
            {
                entity[prop.Name] = root.TryGetProperty($"{prop.Name}@odata.type", out var edm)
                    ? ConvertAnnotatedValue(prop.Value, edm.GetString())
                    : ConvertPlainValue(prop.Value);
            }
            else
            {
                entity[prop.Name] = ConvertJsonValue(prop.Value);
            }
        }

        return entity;
    }

    // Typed-format value with an explicit EDM annotation.
    private static object? ConvertAnnotatedValue(JsonElement element, string? edmType)
    {
        if (element.ValueKind is JsonValueKind.Null) return null;
        return edmType switch
        {
            "Edm.Guid" => Guid.Parse(element.GetString()!),
            "Edm.DateTime" => element.GetDateTimeOffset(),
            "Edm.Binary" => element.GetBytesFromBase64(),
            "Edm.Int64" => element.GetInt64(),
            "Edm.Double" => element.GetDouble(),
            _ => ConvertPlainValue(element),
        };
    }

    // Typed-format value WITHOUT an annotation: the JSON kind is authoritative (strings stay strings).
    private static object? ConvertPlainValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt32(out var i32) ? i32
            : element.TryGetInt64(out var i64) ? i64
            : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    // Legacy (pre-"@v") rows: infer types from value shape — imperfect by construction, kept only so
    // old backup files remain restorable.
    internal static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => TryParseTypedString(element.GetString()!),
        JsonValueKind.Number => element.TryGetInt32(out var i32) ? i32
            : element.TryGetInt64(out var i64) ? i64
            : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static object TryParseTypedString(string value)
    {
        // Try DateTimeOffset for ISO 8601 strings (19-35 chars, starts with digit)
        if (value.Length is >= 19 and <= 35 && char.IsDigit(value[0]) &&
            DateTimeOffset.TryParse(value, out var dto))
            return dto;

        // Try Guid (36 chars with dashes)
        if (value.Length == 36 && Guid.TryParse(value, out var guid))
            return guid;

        return value;
    }

    private static string? ExtractTableName(string fileName)
    {
        // "Users.jsonl" → "Users", "Users.jsonl.gz" → "Users"
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.EndsWith(".jsonl")) name = name[..^6];
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

public sealed class RestoreResult
{
    public Dictionary<string, RestoreTableResult> Tables { get; set; } = new();
    public long TotalRestored => Tables.Values.Sum(t => t.Restored);
    public long TotalErrors => Tables.Values.Sum(t => t.Errors);
    /// <summary>Deletes applied from the backup's <c>_tombstones</c> file (0 for full backups).</summary>
    public long TombstonesApplied { get; set; }
}

public sealed class RestoreTableResult
{
    public long Restored { get; set; }
    public long Errors { get; set; }
}
