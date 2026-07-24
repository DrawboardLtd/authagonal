---
layout: default
title: Migration
---

# Migration from Duende IdentityServer

The `Authagonal.Migration` package performs a one-time migration from Duende IdentityServer + SQL
Server into Authagonal's stores. The same engine is available two ways:

- **Hosted runner** (recommended) — a background service inside your Authagonal host that runs the
  migration once on deploy, gated on cluster leadership, without blocking startup.
- **CLI** — `tools/Authagonal.Migration.Cli`, for local/offline runs against a Table Storage target.

SqlClient lives only in this package, so hosts that don't migrate never inherit it.

## Hosted runner

Add it after `AddAuthagonal` (it depends on the stores, the secret provider, and cluster leadership):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);
```

Configure via the `Migration` section:

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

The runner:

1. Waits `StartupDelaySeconds` (seed services finish first; startup is never blocked).
2. Skips if a `Completed`, non-`DryRun` marker already exists for `Version`.
3. Waits up to `LeaseWaitMinutes` to become cluster leader (only one pod runs the migration).
4. Writes a `Started` marker, runs the engine, then a `Completed`/`Failed` marker with the report.

Losing leadership mid-run cancels the engine; the new leader re-runs — safe because every pass is
idempotent. Check progress at `GET /admin/migration/status` (gated by the `IdentityAdmin` policy).

## CLI

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(No `--` separator after the image name.) Or from source:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## What gets migrated

| Source (SQL Server) | Target | Notes |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + email/name indexes | Ids preserved verbatim. Claim folding: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (xmlsoap variants too); email claims dropped; everything else → custom attributes. Null password hashes (external-SSO-only users) are fine. BCrypt / ASP.NET Identity V3 hashes verify unchanged and upgrade to native PBKDF2 on next login. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = skip (idempotent) |
| `AspNetRoles` + `AspNetUserRoles` | Roles + user role links | Role id→name map resolves user assignments |
| `ApiScopes` + `IdentityResources` | Scopes | Existing (seed) names skipped; scope claims copied |
| Duende `Clients` + child tables | Clients | Secrets tagged `SHA256$`/`SHA512$` by digest length (others dropped with a warning); expired secrets skipped; config-seeded clients win (skipped) |
| Duende `ApiResources` | (flattened) | Audiences → migration-created clients; resource claims → migration-created scopes |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV split into SSO domain records |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | Same domain splitting |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | TOTP secret base32→protected (`duende-totp`); recovery codes hashed (`duende-rc-{n}`); user skipped if MFA already present |
| Duende `PersistedGrants` (refresh tokens) | Grants | Opt-in via `MigrateRefreshTokens`; non-expired only. If skipped, users re-login. |

## Options

| Option | Default | Description |
|---|---|---|
| `Enabled` | `false` | Master switch for the hosted runner |
| `DryRun` | `false` | Walk the source and produce the full validation report (id charset/length, duplicate emails, table/column inventory, per-pass counts) without writing |
| `Version` | `"1"` | Run marker. Bump to re-run a delta sweep. Only a `Completed`, non-`DryRun` marker blocks a re-run |
| `UsersMode` | `CreateOnly` | `CreateOnly` skips existing users; `Upsert` overwrites. **Never `Upsert` post-cutover** — it clobbers rehashed passwords and new MFA |
| `MigrateClients` | `true` | Migrate OAuth clients |
| `MigrateRefreshTokens` | `false` | Include active refresh tokens |

## Idempotency & delta sweeps

Every pass is idempotent (skip-if-exists, deterministic MFA ids), so the migration is safe to re-run.
Run it days ahead of cutover, then bump `Version` for a final delta sweep close to cutover to pick up
users registered since. Existing records are skipped (or updated under `Upsert`), never duplicated.

## What is NOT migrated

- **SCIM tokens and groups**, **user provisions** — no Duende equivalent; start empty.
- **Signing keys** — not automated. To keep existing tokens valid across cutover, export the RSA
  signing key from Duende and import it into the `SigningKeys` table close to cutover.

## Cutover strategy

1. Deploy dark (`Enabled=false`).
2. `Enabled=true, DryRun=true` → restart → review the report at `/admin/migration/status`.
3. `DryRun=false` → restart → verify the marker is `Completed` + spot-check logins.
4. Bump `Version` for the final delta sweep, then repoint clients/BFFs to Authagonal (one forced
   re-login expected unless refresh tokens were migrated).
5. Monitor; rollback = repoint to the untouched Duende deployment.
