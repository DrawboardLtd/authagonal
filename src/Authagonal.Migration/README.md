# Authagonal.Migration

One-time migration from **Duende IdentityServer** (SQL Server) into **Authagonal**'s stores.

Ships three things:

- **`DuendeMigrationEngine`** — reads a Duende `ConfigurationDb`/`IdentityDb` and writes users, external
  logins, roles, scopes, clients, API-resource flattening, SAML/OIDC providers, SSO domains, MFA
  credentials, and (optionally) refresh tokens through Authagonal's store abstractions. Product-agnostic:
  it writes stores directly and never triggers host provisioning callbacks.
- **`DuendeMigrationHostedRunner`** — a leader-gated `BackgroundService` that runs the engine exactly
  once per configured `Version` on deploy, without blocking startup. Wire it with
  `services.AddAuthagonalDuendeMigration(configuration)` after `AddAuthagonal`.
- **CLI support** — the same engine drives `tools/Authagonal.Migration.Cli` for local/offline runs.

## Configuration (`Migration` section)

```json
{
  "Migration": {
    "Enabled": false,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=...;" }
  }
}
```

- **`DryRun`** produces the full validation report (id charset/length, duplicate emails, table/column
  inventory, per-pass counts) and writes nothing.
- **`Version`** is the run marker's RowKey — bump it to re-run a delta sweep. Only a `Completed`,
  non-`DryRun` marker blocks a re-run.
- **`UsersMode`** — `CreateOnly` (default; skip existing) or `Upsert` (overwrite). **Never `Upsert`
  post-cutover** — it would clobber rehashed passwords and new MFA.

## Status endpoint

`GET /admin/migration/status` returns the latest marker + last report, gated by the `IdentityAdmin`
authorization policy like the other admin endpoints.
