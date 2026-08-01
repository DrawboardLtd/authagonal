# Authagonal.Backup

Programmatic backup, restore, merge, and rollup for Authagonal's Azure Table Storage data. This is the library that backs the `Authagonal.Backup` CLI, use it directly when you need the same operations from inside a host process (background services, custom orchestration).

## Quick start

```csharp
using Authagonal.Backup;
using Azure.Data.Tables;

var serviceClient = new TableServiceClient(connectionString);

// Backup
var backupOptions = new BackupOptions
{
    Tables = BackupDefaults.Tables,
    Incremental = false,
    Gzip = true,
};
var target = new FileSystemBackupTarget("./backups");
var backup = new BackupService(serviceClient, target, backupOptions);
await backup.RunAsync(ct);

// Restore
var restoreOptions = new RestoreOptions { Mode = RestoreMode.Upsert, ManifestKey = manifestKey };
var source = new FileSystemBackupSource("./backups/20260426-120000");
var restore = new RestoreService(serviceClient, source, restoreOptions);
var result = await restore.RunAsync(ct);
```

## Integrity

The manifest records a SHA-256 per file, and restore verifies each one from the same read it applies
the entities from. Those hashes establish that the archive matches the manifest — not that either is
authentic, because the manifest sits on the same target as the data: whoever can rewrite
`Clients.jsonl.gz` can rewrite the line recording its hash.

`BackupOptions.ManifestKey` (CLI: `--manifest-key`) closes that by HMAC-ing the manifest, and
`RestoreOptions.ManifestKey` verifies it. **Keep the key outside the backup target** — a MAC key
stored beside the archive reproduces exactly the circularity it exists to remove.

Restore fails closed: with no `ManifestKey` it refuses rather than warning, and
`AllowUnauthenticatedManifest` is the explicit opt-out for an archive written before manifest signing.
A file listed in the manifest but missing from the store aborts the restore, as does one present but
unlisted.

## Surface

| Type | Purpose |
|---|---|
| `BackupService` | Reads tables, writes JSONL (optionally gzipped) plus a `_manifest.json` with per-file SHA-256 hashes |
| `RestoreService` | Verifies hashes, decompresses gzip, writes back into Table Storage in `Upsert` / `Merge` / `Clean` modes |
| `MergeService` | Combines multiple backup sets into one |
| `RollupService` | Folds incrementals into a fresh full backup |
| `IBackupSource` / `IBackupTarget` | Abstractions for plugging in non-filesystem stores |
| `FileSystemBackupSource` / `FileSystemBackupTarget` | Default filesystem implementations |
| `BackupManifest` / `TableBackupInfo` | The serialized manifest schema |
| `BackupDefaults` | Default table list, every persistent Authagonal table, transient ones excluded |
| `BackupOptions` / `RestoreOptions` / `RestoreMode` | Per-run configuration |

## See also

- [Backup & Restore docs](https://authagonal.github.io/authagonal/backup-restore.html), full CLI reference, scheduling, Docker images.

## Packages

| Package | Description |
|---------|-------------|
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |
| [Authagonal.AzureProvider](https://www.nuget.org/packages/Authagonal.AzureProvider) | Azure Table Storage backend |
| **Authagonal.Backup** | Backup/restore/merge/rollup library and CLI |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server, endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/authagonal/authagonal)
- [Documentation](https://authagonal.github.io/authagonal)
