# Authagonal.SqlProvider

Self-hosted SQL storage for [Authagonal](https://github.com/authagonal/authagonal) — **PostgreSQL** for
production, **SQLite** for a single file with no server at all. No cloud account, no emulator, no
managed service.

Implements the full `Authagonal.Core.Stores` surface, the clustering seams (`ILeaseProvider`,
`IClusterEventBus`), and DataProtection key-ring persistence — the same shape as
`Authagonal.AzureProvider` and `Authagonal.AwsProvider`, so switching backends is a wiring change.

## Quick start

```csharp
// PostgreSQL — call BEFORE AddAuthagonal
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");
builder.Services.AddAuthagonal(builder.Configuration);
```

```csharp
// SQLite — one file, nothing to run
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");
builder.Services.AddAuthagonal(builder.Configuration);
```

Tables are created on startup if absent (every statement is `IF NOT EXISTS`, so it is safe to race
across pods and a no-op against a schema you provisioned yourself).

This package is **not** a dependency of `Authagonal.Server` — referencing the server library never pulls
Npgsql or the SQLite native binaries into an Azure- or AWS-only application. Reference it explicitly
when you want a SQL backend, exactly as with `Authagonal.AwsProvider`.

## Which one

|  | PostgreSQL | SQLite |
|---|---|---|
| Multiple pods / HA | yes | no — one writer by construction |
| Leader election, cluster event bus | `UseSql()` | in-process (the default) is correct |
| Ops | a server to run and back up | a file to back up |
| Good for | production self-hosting | quick start, embedded hosts, CI, small deployments |

## Layout

Every table is the same generic shape, which is what keeps the key scheme identical to Azure Table
Storage's PartitionKey/RowKey and DynamoDB's HASH/RANGE:

```sql
CREATE TABLE "Users" (
    pk         TEXT   COLLATE "C" NOT NULL,   -- partition key
    sk         TEXT   COLLATE "C" NOT NULL,   -- sort key
    data       TEXT,                          -- the document (JSON, possibly encrypted)
    attrs      JSONB  NOT NULL DEFAULT '{}',  -- promoted, queryable fields
    version    BIGINT NOT NULL DEFAULT 0,     -- optimistic concurrency
    expires_at TEXT   COLLATE "C",            -- optional TTL
    PRIMARY KEY (pk, sk)
);
```

Table names match the other backends one-for-one, so a backup taken on Azure or AWS restores here
without renaming anything.

`COLLATE "C"` is load-bearing. The key scheme is byte-ordinal throughout — prefix bounds, the
env-partition range, the grant expiry sweep, keyset paging — and a database created with a linguistic
collation (`en_US.UTF-8` and ICU locales are the common defaults) orders punctuation and case
differently. Those scans would then silently return the wrong rows: expired grants stop being reaped,
prefix search misses matches. Pinning the collation per column makes the layout independent of how the
database was created. The test suite runs against an ICU-collated database on purpose.

## Concurrency

The operations an auth server cannot get wrong are each a single statement — no read-modify-write
window, no explicit transaction, no lock held across a round trip:

| Guarantee | Mechanism |
|---|---|
| Authorization code / MFA challenge / OIDC state redeemable once | `DELETE … RETURNING` |
| Refresh rotation marks consumed exactly once | `UPDATE … WHERE consumedAt IS NULL` |
| Lockout counter loses no increments | `UPDATE … WHERE version = @v`, re-read and retry |
| SAML assertion replay detected | `INSERT … ON CONFLICT DO NOTHING` |
| At most one lease holder | `INSERT … ON CONFLICT DO UPDATE … WHERE expired OR mine` |

## TTL

Neither backend expires rows on its own the way DynamoDB TTL does, so `SqlExpiryReaper` (registered
automatically) sweeps the transient tables — SAML replay ids, OIDC state, MFA challenges, upstream
refresh tokens, the revocation list. Those rows are already ignored on read once expired; the sweep is
space reclamation, not correctness. Grant expiry is deliberately *not* handled there: grants span
three tables that must be cleaned together with their tombstones, so `IGrantStore.RemoveExpiredAsync`
stays the single owner.

## Encryption at rest

The `IFieldCipher` / `IIndexTokenizer` seams work exactly as on the other backends. Register them
before `AddSqlStorage` and the user document is encrypted and lookup keys become blind-index tokens;
leave them out and the layout is plaintext. Rows written before you turned encryption on keep
resolving, and `IUserStore.ReindexUserAsync` backfills them — so it can be switched on without
downtime. For key material, pair with the HashiCorp Vault Transit signer.

The same seam covers the `SigningKeys` table, whose `keyMaterialJson` is the *private* half of the
token-signing key. That one is worth calling out, because the SQL backend changes its blast radius. On
Azure the signing key is in Table Storage and the DataProtection ring in a Blob container, each with its
own grantable RBAC; on AWS, DynamoDB and S3. Here everything is one database behind one connection
string, so **treat that connection string as equivalent to the signing key itself** — a `pg_dump` handed
to a developer, a read replica, an analytics role with `SELECT`, or a restored backup is otherwise
enough to mint access tokens and id_tokens for any subject, scope and client. Registering a cipher is
what breaks that equivalence. Keys written before you did keep loading, and re-protect themselves at the
next rotation.

Passthrough stays the default — the quick start has no key management to hang a cipher off — but it is
no longer silent: outside `Development`, startup logs a `Warning` naming the table and the remedy when
no `IFieldCipher` is registered. The key ring below has always announced itself; the more valuable
secret beside it did not.

## DataProtection keys

`AddAuthagonalPostgres` / `AddAuthagonalSqlite` persist the ASP.NET DataProtection key ring to the same
database by default, so cookies and antiforgery tokens survive restarts and work across pods with no
extra service. Persisting is not encrypting: unless DataProtection has an `IXmlEncryptor`, each row
holds a bare `<masterKey>`, and read access to `DataProtectionKeys` is the ability to forge and decrypt
auth cookies. Configure one of:

| Setting | Effect |
|---|---|
| `DataProtection:KeyVaultKeyId` | Wraps the ring with an Azure Key Vault key (`DefaultAzureCredential`). |
| `DataProtection:CertificateThumbprint` | Wraps the ring with a certificate from the machine store. |
| `DataProtection:AllowUnencryptedKeyRing` | Explicitly accepts a plaintext ring; logged at Critical on every start. |

Set one of them deliberately, because startup checks for it. The check reads the *resolved* key-ring
options rather than configuration, so it sees the repository this package attaches just as well as the
Azure one, and the verdict is the same on every backend:

- **Encrypted and persisted** — starts silently.
- **Persisted, unencrypted, ring is empty** — refuses to start. Nothing depends on the ring yet, so the
  insecure state never gets created.
- **Persisted, unencrypted, ring already has keys** — starts, and logs at `Critical` every time. An
  existing deployment's cookies are encrypted under those keys, so refusing on a version bump would be
  an outage; fix it forward with one of the settings above.
- **Development** — never refuses. The quick start runs on SQLite with no key on purpose.

The complementary control is to stop the key ring sharing a blast radius with the application data at
all. `PersistDataProtectionKeysToSql` takes its own `SqlDataSource`, so it can point at a separate
schema under a separately-granted role, leaving a `SELECT` on the application schema with nothing
key-shaped in it:

```csharp
builder.Services.AddAuthagonalPostgres(appConnectionString, persistDataProtectionKeys: false);
builder.Services.PersistDataProtectionKeysToSql(
    new SqlDataSource(new PostgresDialect(keyRingConnectionString, schema: "authagonal_keys")));
```

## Clustering (PostgreSQL)

```csharp
builder.Services.AddAuthagonal(builder.Configuration, clustering =>
    clustering.UseSql(dataSource));      // leadership + event bus
    // or clustering.UseSqlBus(dataSource) on nodes that must not contend for leadership
```

Leadership is a conditional-upsert lease row; the event bus is an append-only log each node polls.
`LISTEN`/`NOTIFY` would be lower-latency but needs a dedicated long-lived connection per node and drops
what a disconnected listener missed — polling a durable log keeps the at-least-once delivery guarantee
identical to the other backends.

## Custom dialects

`ISqlDialect` is small: a connection, the DDL, a JSON accessor, and how to qualify a table name.
Everything else is SQL both engines accept verbatim. Implement it and pass your own `SqlDataSource` to
`AddAuthagonalSqlStorage` to target another engine.
