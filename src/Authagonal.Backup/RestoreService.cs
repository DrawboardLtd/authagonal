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
        else if (options.VerifyIntegrity && !options.AllowUnauthenticatedManifest)
        {
            // Refused, not warned — the same correction the AllowUnverified branch above already got.
            // Authentication that fails open authenticates nothing: without a key the hashes only prove
            // the archive matches a manifest that sits beside it on the same target, so an attacker who
            // rewrites Clients.jsonl.gz rewrites its recorded hash in the same breath and the restore
            // reports success. A Console.Error line inside a host process, or a pipeline that discards
            // stderr, is not a decision anybody made.
            throw new InvalidOperationException(
                "No ManifestKey supplied, so the manifest is unauthenticated: its file hashes detect " +
                "corruption but not tampering, because anyone who can rewrite a backup file can rewrite " +
                "the hash recorded beside it. Supply the ManifestKey this backup was written with, or " +
                "set AllowUnauthenticatedManifest if the backup predates manifest signing and " +
                "unauthenticated data is acceptable.");
        }

        // Envelope: unwrap the content key, and refuse a downgrade.
        //
        // A caller who supplies an EncryptionKey has said this deployment's backups are encrypted, so
        // a plaintext archive is either older than that or has been substituted for one. Reading it
        // anyway would make the encryption trivially downgradeable — drop a plaintext archive into the
        // target and restore accepts it without a word.
        byte[]? contentKey = null;
        if (options.EncryptionKey is { Length: > 0 } kek)
        {
            if (string.IsNullOrEmpty(manifest?.WrappedContentKey))
            {
                if (!options.AllowUnencrypted)
                    throw new InvalidOperationException(
                        "An EncryptionKey was supplied but this backup is not encrypted. Restore it with " +
                        "AllowUnencrypted if it predates backup encryption; otherwise treat the archive as " +
                        "substituted.");
            }
            else
            {
                contentKey = BackupEncryption.UnwrapKey(manifest.WrappedContentKey, kek);
            }
        }
        else if (!string.IsNullOrEmpty(manifest?.WrappedContentKey))
        {
            throw new InvalidOperationException(
                "This backup is encrypted but no EncryptionKey was supplied. Provide the same " +
                "key-encryption key the backup was written with.");
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

            // Refuse rather than warn. This used to write to Console.Error — the anti-pattern this same
            // file rejects three screens up, because a library writing to stderr inside a host process
            // is not a signal anyone sees, and the operator who most needs it is the one running this
            // from a pipeline that discards it. The adjacent incremental guard above shows the shape.
            //
            // There is no way to detect a shared table set from here, so the choice is between
            // destroying every env's rows on a maybe and requiring one explicit flag. AllowCleanAllEnvs
            // is that flag: a deployment with one env per table set says so once.
            if (options.CleanEnvPrefix is null && !options.DryRun && !options.AllowCleanAllEnvs)
                throw new InvalidOperationException(
                    "Refusing a Clean restore with no CleanEnvPrefix: this empties the ENTIRE target table(s), " +
                    "including every other env sharing them. Set CleanEnvPrefix to scope the wipe to one env, " +
                    "or pass AllowCleanAllEnvs if these tables genuinely hold a single env.");
        }

        // A file the manifest lists but the store does not hold is tampering too, and it was invisible:
        // the loops below iterate what the SOURCE offers, so deleting Clients.jsonl.gz outright — or the
        // tombstone file carrying a set of GDPR erasures — produced a restore that verified every file
        // it found and reported success. Detecting removal needs the manifest to be the authority on
        // what should be present.
        if (canVerify)
        {
            var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var missing = manifest!.FileHashes.Keys.Where(f => !present.Contains(f)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Backup integrity check failed: the manifest lists {missing.Count} file(s) that are not " +
                    $"present in the backup ({string.Join(", ", missing.Take(5))}). The archive is incomplete " +
                    "or has been tampered with.");
        }

        foreach (var fileName in files)
        {
            if (fileName.StartsWith("_")) continue; // Skip metadata files (manifest, tombstones)

            var tableName = ExtractTableName(fileName);
            if (tableName is null) continue;

            // The archive does not get to choose which tables exist.
            //
            // The destination was derived purely from the file name, and RestoreOptions.Tables is null by
            // default (the CLI leaves it unset), so the set of tables written was whatever the archive named
            // — with GetTableClient(prefix + name) + CreateIfNotExists behind it. A hand-made or tampered
            // archive could therefore create and populate any table it liked in the target account.
            if (!BackupDefaults.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Backup integrity check failed: '{fileName}' names table '{tableName}', which is not part "
                    + "of the backup set. An archive does not get to choose which tables a restore writes.");

            // And it does not get to overturn the backup's own decision about signing keys. BackupOptions
            // .IncludeSigningKeys is off by default and BackupService refuses to write them; restoring one
            // from an archive that carries it anyway installs JWT signing private keys into a live
            // deployment. Both halves of the pair now have to be turned on deliberately.
            if (tableName.Equals("SigningKeys", StringComparison.OrdinalIgnoreCase) && !options.IncludeSigningKeys)
            {
                result.SkippedSigningKeys = true;
                continue;
            }

            if (options.Tables is not null && !options.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                continue;

            // Verify the entire file against the manifest BEFORE applying any of its entities — and
            // before the Clean wipe below — so a tampered backup can't inject an admin client, reset a
            // password hash, or plant a signing key. A file absent from the manifest is treated as
            // tampering.
            string? expectedHash = null;
            if (canVerify && !manifest!.FileHashes.TryGetValue(fileName, out expectedHash))
                throw new InvalidOperationException(
                    $"Backup integrity check failed: '{fileName}' is not listed in the manifest.");

            var stream = await OpenVerifiedAsync(backupId, fileName, expectedHash, ct);
            if (stream is null) continue;

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

            long restored = 0;
            long errors = 0;
            long skipped = 0;

            await using (stream)
            {
                // Decrypt then decompress — the mirror of compress-then-encrypt on the way out.
                Stream plain = contentKey is null ? stream : BackupEncryption.Decrypt(stream, contentKey, fileName);
                await using var decryptScope = ReferenceEquals(plain, stream) ? null : plain;
                Stream readStream = fileName.EndsWith(".gz") ? new GZipStream(plain, CompressionMode.Decompress) : plain;
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

                        // Scope the APPLY, not just the wipe. CleanEnvPrefix bounded which rows were
                        // deleted and then imported the file wholesale, so restoring a backup taken
                        // from a shared sandbox table set — BackupOptions has no env filter, so it
                        // contains every env's rows — wrote every sibling env's rows back into the
                        // target. The wipe was scoped and the restore was not, which is the half of
                        // the finding the scoped delete looked like it had closed.
                        if (options.CleanEnvPrefix is not null
                            && !entity.PartitionKey.StartsWith(options.CleanEnvPrefix, StringComparison.Ordinal))
                        {
                            skipped++;
                            continue;
                        }

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

            result.Tables[tableName] = new RestoreTableResult { Restored = restored, Errors = errors, SkippedOtherEnv = skipped };
        }

        // F24b: honor the backup's deletes. Restoring full + incrementals without applying tombstones
        // resurrects every row deleted in the window (incl. GDPR erasures) — the tombstone file is as
        // much a part of an incremental's state as its data files. Applied AFTER the data files; within
        // one backup a key never has both a live capture and a tombstone (the change-log is upsert-
        // replaced per key, last op wins), and across backups the operator applies them oldest-first, so
        // a later incremental's recreate lands after the earlier delete.
        if (options.ApplyTombstones)
        {
            (result.TombstonesApplied, result.TombstonesSkippedOtherEnv) =
                await ApplyTombstonesAsync(backupId, files, prefix, manifest, canVerify, contentKey, ct);
        }

        return result;
    }

    private async Task<(long Applied, long SkippedOtherEnv)> ApplyTombstonesAsync(
        string backupId, IReadOnlyList<string> files, string prefix,
        BackupManifest? manifest, bool canVerify, byte[]? contentKey, CancellationToken ct)
    {
        var fileName = files.FirstOrDefault(f => f is "_tombstones.jsonl.gz" or "_tombstones.jsonl");
        if (fileName is null) return (0, 0); // full backups (and empty incrementals) carry no tombstone file

        // A tampered tombstone file deletes attacker-chosen rows (e.g. a revocation record), so verify
        // it like a data file when a hash is recorded.
        string? expectedHash = null;
        if (canVerify && !manifest!.FileHashes.TryGetValue(fileName, out expectedHash))
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

        var stream = await OpenVerifiedAsync(backupId, fileName, expectedHash, ct);
        if (stream is null) return (0, 0);

        long applied = 0;
        long skippedOtherEnv = 0;
        await using (stream)
        {
            Stream plain = contentKey is null ? stream : BackupEncryption.Decrypt(stream, contentKey, fileName);
            await using var decryptScope = ReferenceEquals(plain, stream) ? null : plain;
            Stream readStream = fileName.EndsWith(".gz") ? new GZipStream(plain, CompressionMode.Decompress) : plain;
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

                // Same allowlist as the data path. A tombstone line names its own table, so without this the
                // delete half chose its destination from the archive exactly as the write half did.
                if (!BackupDefaults.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Backup integrity check failed: a tombstone names table '{tableName}', which is not "
                        + "part of the backup set.");

                if (tableName.Equals("SigningKeys", StringComparison.OrdinalIgnoreCase) && !options.IncludeSigningKeys)
                    continue;

                if (options.Tables is not null && !options.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Scope the DELETES, exactly as the data path scopes the writes. This is the same finding as
                // the one recorded at the data-apply guard above, on the more destructive half: the wipe was
                // scoped, the apply was scoped, and the tombstone pass was not.
                //
                // BackupTombstonesAsync filters the change log on Timestamp alone — BackupOptions has no env
                // filter — so on a shared sandbox table set the file holds every env's deletes, with the
                // `{env}|` prefix intact in the authoritative OrigPK column it writes as PartitionKey. An
                // operator restoring sandbox-1 therefore executed sandbox-2..N's deletes against live tables
                // that were never in scope, and unlike MergeService.IsDeleted this path has no recreate or
                // timestamp guard, so rows a sibling env created AFTER the backup was taken went too. Deleting
                // a revocation record also resurrects a revoked token. None of it is recoverable from the
                // archive being restored, because those rows are not in it.
                if (options.CleanEnvPrefix is not null
                    && !pk.StartsWith(options.CleanEnvPrefix, StringComparison.Ordinal))
                {
                    skippedOtherEnv++;
                    continue;
                }

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

        return (applied, skippedOtherEnv);
    }

    /// <summary>
    /// Opens a backup file over exactly the bytes that were hashed — see <see cref="VerifiedRead"/>, which the
    /// rollup path shares so that both readers verify by the same rule.
    /// </summary>
    private Task<Stream?> OpenVerifiedAsync(
        string backupId, string fileName, string? expectedHash, CancellationToken ct)
        => VerifiedRead.OpenAsync(source, backupId, fileName, expectedHash, ct);

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

    /// <summary>
    /// True when the archive carried a <c>SigningKeys</c> file that was NOT applied, because
    /// <see cref="RestoreOptions.IncludeSigningKeys"/> was off.
    /// </summary>
    /// <remarks>
    /// Reported rather than silent: an operator restoring an archive that contains signing keys needs to know
    /// the keys were left out, since the alternative reading — "the restore was complete" — is wrong in a way
    /// that only shows up when tokens minted under the old key fail to validate.
    /// </remarks>
    public bool SkippedSigningKeys { get; set; }

    /// <summary>
    /// Deletes in the backup's <c>_tombstones</c> file that belong to another env and were NOT applied.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="RestoreTableResult.SkippedOtherEnv"/> for the delete pass. Non-zero is
    /// normal when restoring one env out of a shared table set — the tombstone file is not env-scoped, because
    /// the change-log scan that produces it filters on Timestamp alone — and reporting it is what tells an
    /// operator that siblings' deletes were declined rather than silently executed.
    /// </remarks>
    public long TombstonesSkippedOtherEnv { get; set; }
}

public sealed class RestoreTableResult
{
    public long Restored { get; set; }
    public long Errors { get; set; }

    /// <summary>
    /// Rows in the backup that belong to another env and were not applied, because
    /// <c>RestoreOptions.CleanEnvPrefix</c> scopes the restore as well as the wipe. Non-zero is normal
    /// when restoring one env out of a shared table set; it is the count that says so out loud.
    /// </summary>
    public long SkippedOtherEnv { get; set; }
}
