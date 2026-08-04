---
layout: default
title: Backup & Restore
---

# Backup & Restore

Authagonal provides two CLI tools for backing up and restoring Azure Table Storage data. Both are .NET console applications in the `tools/` directory, and both are thin wrappers over the `Authagonal.Backup` NuGet package. Hosts that need scheduled, multi-tenant, or non-filesystem backups can use the library directly (see [Using the library](#using-the-library)).

## Backup

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Azure Table Storage connection string (or set `STORAGE_CONNECTION_STRING` env var) |
| `--output <dir>` | Output directory (default: `./backups`) |
| `--incremental` | Only back up entities changed since last backup |
| `--tables <t1,t2,...>` | Comma-separated list of tables (default: all Authagonal tables) |
| `--prefix <prefix>` | Table name prefix (for multi-tenant storage) |
| `--gzip` | Compress backup files with gzip (`.jsonl.gz`) |
| `--encryption-key <base64>` | 32-byte AES-256 key-encryption key. Encrypts every data file. Keep it **outside** the backup target. |
| `--manifest-key <base64>` | ≥32-byte HMAC key. Signs the manifest so restore can prove the recorded hashes were not rewritten with the files. Keep it **outside** the backup target. |
| `--dry-run` | Show what would be backed up without writing |

### Output format

Each backup creates a timestamped directory:

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

Each `.jsonl` file contains one JSON object per line (one per table entity). With `--gzip`, files are compressed as `.jsonl.gz`. The `_manifest.json` records the backup id, timestamp, mode (`full` or `incremental`), compression, the incremental watermark, per-table entity counts, the tombstone count, which tables (if any) were read via the change-log (`ChangeLogTables`, null means full scan coverage), and SHA-256 file hashes for integrity verification.

Incremental backups also write a `_tombstones.jsonl(.gz)` file recording deletes since the watermark: one line per deleted row with `Table`, `PartitionKey`, `RowKey`, and `DeletedAt`. Restore replays these so deleted rows are not resurrected (see [Tombstone replay](#tombstone-replay)).

Entity values round-trip exactly: each backed-up row carries a `"@v"` format marker and an explicit `"{column}@odata.type"` annotation (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) for every column JSON can't represent unambiguously, so restore writes back the original types rather than stringified or re-inferred values.

### Integrity verification

Each backup manifest includes a `FileHashes` dictionary mapping filenames to their SHA-256 hashes. During restore each file is verified against its recorded hash — from the same read the entities are applied from, so the bytes that were checked are the bytes that get written — before any of its data reaches a table. A file that fails the check, a data file absent from the manifest, or a manifest-listed file missing from the store all abort the restore. Backups written before integrity hashing existed (no `FileHashes`) cannot be verified and are refused unless `--allow-unverified`. Verification can be disabled programmatically via `RestoreOptions.VerifyIntegrity` (default `true`).

Hashes establish that the archive matches the manifest, not that either is authentic: the manifest sits on the same target as the data, so whoever can rewrite `Clients.jsonl.gz` can rewrite the line recording its hash. `--manifest-key` closes that — the backup HMACs the manifest, the restore verifies it, and the key lives somewhere the backup writer cannot reach. **Restore fails closed**: with no `--manifest-key` it refuses rather than warning, and `--allow-unauthenticated-manifest` is the explicit opt-out for archives written before manifest signing.

### Incremental backups

Pass `--incremental` to only back up entities modified since the last successful backup. The tool uses Azure Table Storage's built-in `Timestamp` property for filtering and tracks the high-water mark in a `.lastbackup` file in the output directory.

If no `.lastbackup` file exists, the first incremental run performs a full backup.

Every incremental `Timestamp` filter subtracts a small safety margin (`BackupDefaults.WatermarkSkewMargin`, 5 minutes) before filtering. The watermark comes from the caller's clock while row timestamps are stamped by the storage service, so a mutation committing inside the clock skew would otherwise be missed by this run and every later one. Re-reading the margin costs a few duplicate rows per run, which restore's upsert semantics dedupe.

### Default tables

The backup tool includes all Authagonal tables by default (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Transient tables (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) are excluded by default since their entries are bounded by token lifetimes; include them explicitly with `--tables` if needed. The `Tombstones` change-log table is handled separately by the backup engine and should not be listed.

### Signing keys are excluded by default

The `SigningKeys` table is in the default table list but **filtered out of backups by default** (`BackupOptions.IncludeSigningKeys`, default `false`; the CLI never enables it). For hosts using the local (table-stored) key source, this table holds the JWT signing **private key**, and writing it to a plaintext backup file would let anyone who reads the backup forge tokens. This applies to **every** host: JWT signing is not delegated to Vault Transit, so there is no configuration in which the `SigningKeys` table holds no private key.

> ⚠️ Only opt in via `BackupOptions.IncludeSigningKeys` when the backup target is itself encrypted at rest and access-controlled. The same applies to the rest of the backup: with the default **plaintext** secret provider, backups also contain upstream OIDC client secrets and TOTP / MFA seeds in cleartext. See [Configuration → Secret Provider](configuration#secret-provider).

## Restore

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Options

| Option | Description |
|---|---|
| `--connection-string <conn>` | Azure Table Storage connection string (or set `STORAGE_CONNECTION_STRING` env var) |
| `--input <dir>` | Backup directory to restore from |
| `--mode <mode>` | Restore mode: `upsert` (default), `merge`, or `clean` |
| `--tables <t1,t2,...>` | Comma-separated list of tables to restore (default: all `.jsonl`/`.jsonl.gz` files in backup) |
| `--prefix <prefix>` | Table name prefix (for multi-tenant storage) |
| `--clean-env <env>` | With `--mode clean`, wipe only this env's rows (PartitionKey prefix `<env>|`) |
| `--allow-clean-from-incremental` | Permit `--mode clean` against an incremental backup |
| `--allow-clean-all-envs` | Permit `--mode clean` with no `--clean-env`, emptying the whole table |
| `--encryption-key <base64>` | The 32-byte key-encryption key the backup was written with. Required for an encrypted archive. |
| `--manifest-key <base64>` | The HMAC key the backup was signed with. **Required** unless `--allow-unauthenticated-manifest`. |
| `--allow-unauthenticated-manifest` | Restore without `--manifest-key`, accepting hashes that detect corruption but not tampering |
| `--allow-unverified` | Restore a backup whose manifest carries no file hashes at all |
| `--dry-run` | Show what would be restored without writing |

### Restore modes

| Mode | Behaviour |
|---|---|
| `upsert` | Insert or replace each entity. Existing data is overwritten. |
| `merge` | Insert or merge. Existing properties not in the backup are preserved. |
| `clean` | Delete all existing data in each table before restoring. |

Gzip-compressed backup files (`.jsonl.gz`) are detected and decompressed automatically; no extra flags needed.

### Tombstone replay

After the data files, restore applies the backup's `_tombstones` file: each recorded key is deleted from the restored tables (`RestoreOptions.ApplyTombstones`, default `true`). An incremental's deletes are as much a part of its state as its upserts; skipping them would resurrect deleted rows, including GDPR-erased ones, when restoring a full plus incrementals sequence. Full backups carry no tombstone file. When restoring a full backup followed by incrementals, apply them oldest first so a later recreate lands after an earlier delete. The tombstone file's hash is verified against the manifest like the data files.

### Exact type round-trip

Rows written with the `"@v"` format marker carry explicit EDM type annotations, so restore reconstructs the exact original column types (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); an unannotated string is restored as a string. Legacy backup files without the marker fall back to shape-based inference, kept only so old backups remain restorable (inference can mis-type GUID-shaped or date-shaped string columns).

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (missing arguments, invalid input) |
| `2` | Partial success (some entities had errors) |

## Using the library

The `Authagonal.Backup` NuGet package exposes the same operations programmatically, for background services or custom orchestration:

| Type | Purpose |
|---|---|
| `BackupService` | Runs a full or incremental backup against a `TableServiceClient`, writing to an `IBackupTarget` |
| `RestoreService` | Verifies hashes and writes a backup back into Table Storage |
| `MergeService` | Streams a full backup plus incrementals (and their tombstones) into one current-state view |
| `RollupService` | Folds incrementals into a fresh full backup, optionally deleting the inputs |
| `BackupOptions` / `RestoreOptions` | Per-run configuration |
| `BackupDefaults` | Default table list and change-log presets |
| `IBackupSource` / `IBackupTarget` | Storage abstractions; `FileSystemBackupSource` / `FileSystemBackupTarget` are the built-in implementations. Implement `IBackupTarget` to write to blob storage or elsewhere. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Change-log-driven incrementals

Azure Table Storage only indexes `PartitionKey` and `RowKey`, so an incremental backup filtered on `Timestamp` is still a full scan of each table. To avoid that, Authagonal's stores record every mutation in a change-log via the `IChangeWriter` seam (`Authagonal.Core`), implemented for Azure by `TableChangeWriter` (`Authagonal.AzureProvider`). It is one physical table, still named `Tombstones`: PK = the logical table name, RK = `"{pk}|{rk}"`, an `Op` column of `"U"` (upsert) or `"D"` (delete), and authoritative `OrigPK`/`OrigRK` columns (a `|` inside the original PartitionKey makes splitting the composite RowKey ambiguous, so the backup reader trusts the columns and only falls back to the split for legacy rows). Each key holds one row (upsert-replace), so the last operation in a backup window wins.

With the change-log path enabled, an incremental backup enumerates a table's `Op = "U"` change-log entries since the watermark and point-reads each live row instead of scanning the table. The feature is **opt-in and off by default**: `BackupOptions.ChangeLoggedTables` null or empty means every table stays on the scan path, so the mechanism ships inert until a deliberate flip (a deploy can't silently miss rows changed by pre-capture code). Two presets:

| Preset | Contents |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | The tables whose writes are fully change-log captured |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | The same set plus `Users`. Users' login-state writes are deliberately not captured (hot path, low value), so this preset is **only safe when you also run the full-scan backstop below** |

The manifest's `ChangeLogTables` property lists which tables a run read via the change-log; null or empty means the run had full scan coverage (a full backup, a plain scan incremental, or a backstop scan).

### Full-scan backstop

Because change-log capture can miss writes (login-state fields, non-store writers, pods running pre-capture code during a deploy), pair change-log incrementals with a periodic full re-scan. Set `BackupOptions.WatermarkOverride` to the timestamp of the last full-coverage scan and leave `ChangeLoggedTables` unset for that run: the incremental then filters on `Timestamp` across the whole window since that scan, picking up anything the change-log never captured. A daily backstop alongside hourly change-log incrementals is a reasonable cadence. Deletes are the one mutation class with no self-heal (a live-row scan cannot see a row that is gone), which is why stores write the delete tombstone **before** deleting the data row.

All incremental filters, backstop included, subtract `BackupDefaults.WatermarkSkewMargin` (5 minutes) from the watermark; callers that purge the change-log after a backup must bound the purge by the same margin or they delete rows the next run still needs.

### Rollups

`RollupService.RollupAsync` merges a full backup and its incrementals into a new full backup; `RollupAndCleanAsync` additionally deletes the inputs afterwards. The optional `newBackupId` parameter names the result (null derives a timestamp id); a specially retained snapshot (for example a weekly rollup) must pass its id here, since id-based retention lists physical backup ids, not manifests.

During a merge, tombstones apply with timestamp ordering: a delete removes a captured row only when the row's `Timestamp` does not postdate the tombstone's `DeletedAt`. A key deleted early in the window and recreated later has both a tombstone and a live capture, and the recreated row survives the rollup. Legacy tombstones without `DeletedAt` remove unconditionally.

## Docker

The backup tool ships a Dockerfile (`tools/Authagonal.Backup/Dockerfile`) for running in CI or without installing the .NET SDK:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

The restore tool has no image; run it with the .NET SDK (`dotnet run --project tools/Authagonal.Restore`).

## Scheduling backups

For production use, run the backup tool on a schedule (e.g. daily full + hourly incremental):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Hosts embedding the library typically run hourly incrementals with the change-log path on, a daily full-scan backstop, and periodic rollups to bound the incremental chain.
