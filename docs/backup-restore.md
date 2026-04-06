---
layout: default
title: Backup & Restore
---

# Backup & Restore

Authagonal provides two CLI tools for backing up and restoring Azure Table Storage data. Both are .NET console applications in the `tools/` directory.

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
| `--gzip` | Compress backup files with gzip (`.jsonl.gz`) |
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
    _manifest.json
```

Each `.jsonl` file contains one JSON object per line (one per table entity). With `--gzip`, files are compressed as `.jsonl.gz`. The `_manifest.json` records the backup timestamp, mode, compression, and entity counts.

### Incremental backups

Pass `--incremental` to only back up entities modified since the last successful backup. The tool uses Azure Table Storage's built-in `Timestamp` property for filtering and tracks the high-water mark in a `.lastbackup` file in the output directory.

If no `.lastbackup` file exists, the first incremental run performs a full backup.

### Default tables

The backup tool includes all Authagonal tables by default:

`Users`, `UserEmails`, `UserLogins`, `UserExternalIds`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `Roles`

Transient tables (`SamlReplayCache`, `OidcStateStore`) are excluded by default — include them explicitly with `--tables` if needed.

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
| `--dry-run` | Show what would be restored without writing |

### Restore modes

| Mode | Behaviour |
|---|---|
| `upsert` | Insert or replace each entity. Existing data is overwritten. |
| `merge` | Insert or merge. Existing properties not in the backup are preserved. |
| `clean` | Delete all existing data in each table before restoring. |

Gzip-compressed backup files (`.jsonl.gz`) are detected and decompressed automatically — no extra flags needed.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (missing arguments, invalid input) |
| `2` | Partial success (some entities had errors) |

## Docker

Both tools have Docker images for running in CI or without installing the .NET SDK:

```bash
# Backup
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-backup --output /backups

# Restore
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-restore --input /backups/20260329-120000
```

## Scheduling backups

For production use, run the backup tool on a schedule (e.g. daily full + hourly incremental):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
