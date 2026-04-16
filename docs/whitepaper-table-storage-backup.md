# Backing Up Azure Table Storage: A Practical Approach

**How Authagonal implements full and incremental backup for a schemaless NoSQL store**

---

## The Problem

Azure Table Storage is a cost-effective, massively scalable key-value store -- but it offers no native backup facility. There are no snapshots, no point-in-time restore, no export button. If a bad deployment corrupts data, or an operator accidentally deletes a table, recovery depends entirely on whatever you built yourself.

For an identity platform like Authagonal -- where the tables hold users, credentials, OAuth grants, signing keys, SSO configurations, and SCIM provisioning state -- the stakes are high. Losing this data doesn't just break an application; it locks people out.

This paper describes the backup strategy Authagonal uses: how it exports data, how incremental backups work despite Table Storage's limited query model, how deletes are tracked, and how the pieces compose into a production-ready backup pipeline.

## Design Goals

1. **Full and incremental backups.** A daily full backup is fine for small deployments, but at scale, hourly incrementals keep the backup window short and storage costs low.
2. **Faithful round-trip.** Every entity property -- strings, integers, booleans, DateTimeOffsets, GUIDs, binary -- must survive a backup/restore cycle without type coercion or data loss.
3. **Multi-tenant support.** Authagonal uses table name prefixes to isolate tenants (e.g. `acmecorpUsers`, `acmecorpClients`). Backup and restore must be prefix-aware so a single storage account can host many tenants with independent backup schedules.
4. **Pluggable storage.** Backups should work to a local filesystem during development and to blob storage (or any other target) in production, without changing the core logic.
5. **Human-readable output.** When something goes wrong, an operator should be able to open a backup file in a text editor and see what's in it.

## Architecture

The backup system is structured as a .NET library (`Authagonal.Backup`) with thin CLI wrappers for backup and restore operations. The library is separated from the main Authagonal server so it can be used as a standalone tool, in a Docker container, or embedded in a scheduled job.

```
Authagonal.Backup (library)
  BackupService         -- orchestrates full/incremental export
  RestoreService        -- imports backup data into Table Storage
  MergeService          -- consolidates full + incrementals into one snapshot
  RollupService         -- merge + cleanup of old backups
  IBackupTarget         -- write abstraction (filesystem, blob, etc.)
  IBackupSource         -- read abstraction
  FileSystemBackupTarget/Source -- local filesystem implementation

tools/Authagonal.Backup     -- CLI entry point for backup
tools/Authagonal.Restore    -- CLI entry point for restore
```

### Storage Abstraction

The core services never touch the filesystem directly. They operate against two interfaces:

**IBackupTarget** provides four operations: open a writable stream for a backup file, write a manifest, get the last watermark (for incremental scheduling), and set a new watermark.

**IBackupSource** provides the read side: read a manifest, open a readable stream, list backup IDs chronologically, list files within a backup, and delete a backup.

The filesystem implementations are straightforward -- timestamped directories with JSONL files inside -- but the abstraction means swapping to Azure Blob Storage or S3 requires implementing just these two interfaces.

## Full Backup

A full backup iterates over every Authagonal table, queries all entities, and writes them to JSONL files (one JSON object per line, one file per table).

The backup process:

1. Generate a backup ID from the current UTC timestamp (e.g. `20260329-120000`).
2. For each of the 20 default Authagonal tables, query the Azure Table Storage SDK's `QueryAsync<TableEntity>` with a page size of 1,000.
3. Serialize each entity to a flat JSON dictionary preserving all properties, including system properties (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Write each serialized entity as a single line to `{TableName}.jsonl` (or `{TableName}.jsonl.gz` if compression is enabled).
5. Record per-table entity counts and durations in a manifest (`_manifest.json`).
6. Update the `.lastbackup` watermark file with the backup start time.

Tables that don't exist in the storage account are silently skipped (HTTP 404 is caught and ignored). Transient tables like `SamlReplayCache` and `OidcStateStore` are excluded by default since their contents are ephemeral.

### Output Format

```
backups/
  20260329-120000/
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    GrantsBySubject.jsonl
    ...
    _manifest.json
```

A single line in `Users.jsonl` looks like:

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

JSONL was chosen over CSV or a binary format because it preserves the schemaless, heterogeneous nature of Table Storage entities (different entities in the same table can have different properties), is streamable (no need to buffer the entire table in memory), and is directly inspectable with standard tools like `jq` or any text editor.

### Compression

When the `--gzip` flag is set, each JSONL file is wrapped in a GZip stream at `CompressionLevel.Optimal` before writing. The file extension changes to `.jsonl.gz`. The restore tool auto-detects GZip by inspecting the magic bytes (`0x1f 0x8b`) at the start of each file, so no flag is needed during restore.

## Incremental Backup

### The Timestamp Trick

Azure Table Storage automatically maintains a `Timestamp` property on every entity, updated on every insert or replace. This is a server-managed property -- applications cannot set it. The backup system exploits this by filtering queries to `Timestamp gt datetime'{watermark}'`, where the watermark is the start time of the last successful backup.

This means an incremental backup only downloads entities that were created or modified since the previous run. For a system with 500,000 entities where 200 changed in the last hour, the incremental backup transfers 200 rows instead of 500,000.

The watermark is stored in a `.lastbackup` file in the backup root directory. If the file doesn't exist (first run, or after manual cleanup), the backup falls back to a full export. Incremental backup IDs include an `-incr` suffix (e.g. `20260329-180000-incr`) and the manifest records `"mode": "incremental"` with the watermark value that was used for filtering.

### Cost of the Timestamp Filter

It's worth being honest about a limitation: `Timestamp` is not indexed. Azure Table Storage only indexes `PartitionKey` and `RowKey`. A filter on `Timestamp gt datetime'...'` results in a full table scan -- Azure reads every entity server-side and evaluates the predicate before returning matches. The filtering reduces data transfer (only changed entities cross the wire), but not server-side read cost.

More importantly, the current approach scans **all 20 tables** individually, even if only one table had changes. That's 20 full table scans per incremental backup, regardless of how few entities actually changed.

At Authagonal's typical identity-data volumes (tens of thousands of entities, not millions), this is perfectly acceptable -- scans are fast, reads are cheap ($0.00036 per 10,000 transactions), and the operation is read-only with no impact on live traffic. The section on [scaling beyond timestamp scans](#scaling-beyond-timestamp-scans) discusses how this could evolve.

### The Delete Problem

The `Timestamp` filter elegantly captures inserts and updates, but it cannot capture deletes. A deleted entity simply vanishes -- there is no `Timestamp` to filter on, no tombstone left behind by Table Storage itself.

Authagonal solves this with application-level tombstone tracking.

## Tombstone Tracking

Every data store in Authagonal (users, clients, grants, signing keys, SSO domains, SAML/OIDC providers, MFA credentials, SCIM resources, roles) accepts an optional `ITombstoneWriter` dependency. When a store deletes an entity, it writes a tombstone record to a dedicated `Tombstones` table:

| Column | Value |
|---|---|
| `PartitionKey` | Logical table name (e.g. `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | UTC timestamp of the deletion |

This is a lightweight, append-mostly side-channel. The tombstone write is a simple upsert, batched up to Azure's 100-entity transaction limit for bulk operations.

During an incremental backup, after exporting modified entities from each table, the backup service queries the `Tombstones` table for records with `Timestamp > watermark`. These are written to a separate `_tombstones.jsonl` file in the backup directory, with a normalized format:

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

This means an incremental backup captures a complete picture of what changed: entities added/modified (from the per-table JSONL files) and entities deleted (from the tombstones file).

## Merge and Rollup

Over time, a backup directory accumulates one full backup and many incrementals. To restore to the current state, all of them would need to be applied in order. The **MergeService** consolidates them into a single full backup.

The merge algorithm:

1. Load the full backup's entity set for one table at a time (to bound memory usage).
2. Layer each incremental on top in chronological order -- newer values overwrite older ones, keyed by `(PartitionKey, RowKey)`.
3. Apply tombstones: for every `(Table, PartitionKey, RowKey)` tuple in the tombstone files, remove the entity from the merged set.
4. Write the resulting entity set as a new full backup.

The **RollupService** wraps this with cleanup: after a successful merge, it deletes the old full backup and all the incrementals that were folded in. This keeps storage usage from growing without bound.

A typical production schedule might look like:

- **Hourly:** Incremental backup
- **Daily (2 AM):** Full backup
- **Weekly:** Rollup (merge the previous week's daily + hourly incrementals, delete originals)

## Restore

The restore tool reads a backup directory and writes entities back into Azure Table Storage. It supports three modes:

**Upsert** (default): Each entity is inserted or replaced. Existing entities with the same key are overwritten. This is the safest mode for disaster recovery.

**Merge**: Each entity is inserted or merged. Properties present in the backup overwrite the corresponding properties in the existing entity, but properties that exist in the live table but not in the backup are preserved. Useful for partial restores.

**Clean**: All existing entities in each target table are deleted before restoring. This produces an exact replica of the backup state, at the cost of a (potentially slow) full table scan to delete existing data.

### Type Fidelity

A key challenge in round-tripping Table Storage data through JSON is preserving property types. Table Storage natively supports strings, integers (Int32/Int64), doubles, booleans, DateTimeOffset, Guid, and binary. JSON has no native representation for most of these.

The restore service uses heuristics to recover types from their JSON string representation:

- **DateTimeOffset**: Strings that are 19-35 characters long, start with a digit, and parse as ISO 8601 are restored as `DateTimeOffset`.
- **Guid**: Strings that are exactly 36 characters and parse as a GUID are restored as `Guid`.
- **Numbers**: JSON numbers are tried as `Int32`, then `Int64`, then `double`, in that order.
- **Booleans and nulls**: Map directly.

This heuristic approach covers Authagonal's actual data patterns without requiring a schema registry or type annotations in the backup format.

### Error Handling

Restore operations are fault-tolerant at the entity level. If an individual entity fails to write (e.g. due to a transient Azure error), the error count is incremented but the restore continues. The final result reports per-table success and error counts, and the process exits with code `2` for partial success -- distinct from `0` (full success) and `1` (fatal error).

## Multi-Tenancy

Authagonal supports multi-tenant deployments where each tenant's tables are prefixed (e.g. `acmecorpUsers`, `contosoclients`). Both backup and restore accept a `--prefix` flag that is prepended to logical table names when communicating with Azure Table Storage.

This means:
- Backup with `--prefix acmecorp` reads from `acmecorpUsers`, `acmecorpClients`, etc., but writes files named `Users.jsonl`, `Clients.jsonl` (logical names).
- Restore with `--prefix contoso` reads `Users.jsonl` and writes to `contosoUsers`.

This makes it straightforward to clone a tenant's data, migrate between environments, or restore one tenant without affecting others.

## Manifest

Every backup includes a `_manifest.json` file recording:

- **BackupId**: Timestamped identifier (e.g. `20260329-120000` or `20260329-180000-incr`)
- **Mode**: `"full"` or `"incremental"`
- **BackupTimestamp**: When the backup started (UTC)
- **Watermark**: For incrementals, the cutoff timestamp used for filtering
- **Compressed**: Whether files are GZip-compressed
- **Tables**: A dictionary of table names to entity counts and durations
- **TombstoneCount**: Number of tombstone records (incremental only)
- **TotalEntities**: Aggregate entity count across all tables
- **DurationSeconds**: Wall-clock time for the backup run
- **FileHashes**: SHA-256 hashes of each backup file for integrity verification

The manifest serves both as an operational dashboard (how big was the backup? how long did it take? which tables are largest?) and as a safety net (hash verification during restore detects corrupted or tampered files).

## Operational Characteristics

**Backup speed** is bounded by Azure Table Storage query throughput, which is typically 5,000-10,000 entities per second per table. A full backup of 100,000 entities across 20 tables completes in under a minute. Incremental backups of a few hundred changed entities finish in seconds.

**Memory usage** is minimal. The backup service streams entities directly to disk -- it never loads an entire table into memory. The merge service processes one table at a time, loading only that table's entity set. For very large tables (millions of entities), the merge memory footprint is proportional to the largest single table.

**Retry policy** is configured with exponential backoff: 5 retries, starting at 500ms, capped at 30 seconds. This covers the transient throttling that Table Storage applies under heavy load.

**Dry run** mode (`--dry-run`) enumerates entities without writing any files, useful for validating connectivity and estimating backup size before committing to a full run.

## Scaling Beyond Timestamp Scans

The `Timestamp`-based approach is pragmatic at moderate scale, but its cost is proportional to total data size, not to the number of changes. As tables grow, 20 full table scans per incremental backup become increasingly wasteful. The natural evolution is a **unified change log table**.

The insight is that the tombstone mechanism already proves this pattern out for deletes. The `Tombstones` table is a single, compact, cross-table index: every delete across all 20 data tables is recorded in one place, queryable by timestamp. Extending this to cover all mutations -- inserts, updates, and deletes -- would eliminate the need to scan the data tables entirely.

### Change Log Design

A change log table with time-bucketed partition keys would look like:

| PartitionKey | RowKey | Properties |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

The partition key is an hour bucket, so finding all changes since the last backup becomes a set of **partition key point queries** -- the fastest operation Table Storage supports. The backup service would:

1. Query the change log for all hour-bucket partitions since the watermark. This is an indexed operation, not a scan.
2. For each `upsert` entry, fetch the current entity from the data table by its exact `PartitionKey`/`RowKey` -- also an indexed point read.
3. For each `delete` entry, record the tombstone directly from the change log. No need for a separate tombstones table.

This makes backup cost proportional to the number of changes, not the total data size. One query against a compact index table replaces 20 full table scans. It also unifies the tombstone mechanism -- the change log captures creates, updates, and deletes uniformly, so the separate `Tombstones` table becomes redundant.

### Why Not Yet

The tradeoff is write-path overhead. Every mutation in every store would need an additional write to the change log table. The plumbing is mostly there -- the `ITombstoneWriter` is already injected into every store and called on every delete. Widening it to an `IChangeTracker` that fires on upserts too is a straightforward refactor.

But "straightforward" isn't "free." It adds latency to every user-facing operation (one extra Table Storage write), increases storage transactions, and introduces a new consistency concern (what if the data write succeeds but the change log write fails?). At current volumes, the 20 timestamp-filtered scans complete in seconds and cost fractions of a cent. The change log would be the right move if tables grew to millions of entities, but for now, the simpler approach wins.

## Summary

The approach is deliberately simple. Rather than building a complex change-data-capture pipeline or relying on Azure-specific features that may not exist for Table Storage, Authagonal uses the one piece of metadata that Azure *does* guarantee -- the server-managed `Timestamp` -- combined with application-level tombstone tracking for deletes.

The result is a backup system that:

- Produces human-readable, portable JSONL files
- Supports full and incremental modes with automatic watermark management
- Correctly captures creates, updates, *and* deletes
- Handles multi-tenant table prefixing transparently
- Composes cleanly (merge, rollup, selective restore)
- Runs as a standalone tool with no dependency on the Authagonal server

The storage abstraction means the same logic can target local disk, Azure Blob Storage, S3, or any other destination. The format is simple enough that even without the restore tool, an operator could reconstruct data with `jq` and the Azure CLI.
