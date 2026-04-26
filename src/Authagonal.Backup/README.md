# Authagonal.Backup

Programmatic backup, restore, merge, and rollup for Authagonal's Azure Table Storage data. This is the library that backs the `Authagonal.Backup` CLI — use it directly when you need the same operations from inside a host process (background services, custom orchestration).

## Quick start

```csharp
using Authagonal.Backup;
using Azure.Data.Tables;

var serviceClient = new TableServiceClient(connectionString);

// Backup
var backupOptions = new BackupOptions
{
    Tables = BackupDefaults.AllTables,
    Incremental = false,
    Gzip = true,
};
var target = new FileSystemBackupTarget("./backups");
var backup = new BackupService(serviceClient, target, backupOptions);
await backup.RunAsync(ct);

// Restore
var restoreOptions = new RestoreOptions { Mode = RestoreMode.Upsert };
var source = new FileSystemBackupSource("./backups/20260426-120000");
var restore = new RestoreService(serviceClient, source, restoreOptions);
var result = await restore.RunAsync(ct);
```

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
| `BackupDefaults` | Default table list — every persistent Authagonal table, transient ones excluded |
| `BackupOptions` / `RestoreOptions` / `RestoreMode` | Per-run configuration |

## See also

- [Backup & Restore docs](https://authagonal.github.io/authagonal/backup-restore.html) — full CLI reference, scheduling, Docker images.

## Packages

| Package | Description |
|---------|-------------|
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |
| [Authagonal.Storage](https://www.nuget.org/packages/Authagonal.Storage) | Azure Table Storage backend |
| **Authagonal.Backup** | Backup/restore/merge/rollup library and CLI |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server — endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/authagonal/authagonal)
- [Documentation](https://authagonal.github.io/authagonal)
