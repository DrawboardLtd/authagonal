---
layout: default
title: Migration
---

# Migration from Duende IdentityServer

Authagonal includes a migration tool for moving from Duende IdentityServer + SQL Server to Azure Table Storage.

## Running the Migration

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(No `--` separator after the image name: everything after it is passed straight to the tool, and a bare `--` breaks the option parsing.)

Or from source:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## What Gets Migrated

| Source (SQL Server) | Target (Table Storage) | Notes |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + name indexes | Single JOIN query. Claims: given_name, family_name, company, org_id (types overridable, see below). Password hashes kept as-is; ASP.NET Identity V3 and BCrypt hashes verify unchanged and upgrade to Authagonal's native PBKDF2 format on the next successful login. |
| `AspNetUserLogins` | UserLogins (forward + reverse index) | `409 Conflict` = skip (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV split into individual SSO domain records |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Same domain splitting |
| Duende `Clients` + child tables | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins all merged into a single entity |
| Duende `PersistedGrants` (refresh tokens) | Grants + GrantsBySubject + GrantsByExpiry | Opt-in via `--MigrateRefreshTokens true`. Only non-expired tokens. If skipped, users simply re-login. |

## Options

| Option | Default | Description |
|---|---|---|
| `--DryRun` | `false` | Log what would be migrated without writing to storage |
| `--MigrateRefreshTokens` | `false` | Include active refresh tokens. If false, users re-authenticate after cutover. |
| `--Source:ClaimMap:{claim}` | the OIDC claim name itself | Override the `AspNetUserClaims` ClaimType read for a mapped claim, e.g. `--Source:ClaimMap:given_name=FirstName`. Used for `given_name`, `family_name`, `company`, `org_id`. |

## Idempotency

The migration is idempotent and safe to run multiple times. Existing records are updated or skipped, never duplicated. This allows you to:

1. Run the migration days ahead of cutover
2. Run a final delta migration close to cutover
3. Re-run if anything goes wrong

## What Is NOT Migrated

These Authagonal features have no Duende equivalent and start empty after migration:

- **Roles**: RBAC roles and user-role assignments
- **MFA credentials**: TOTP, WebAuthn, and recovery code enrollments
- **SCIM tokens and groups**: SCIM provisioning configuration
- **User provisions**: TCC downstream app provisioning state

Users will need to re-enroll MFA if your client's `MfaPolicy` is `Enabled` or `Required`.

## Signing Key Migration

Not yet automated. To keep existing tokens valid across the cutover:

1. Export the RSA signing key from Duende (typically in appsettings as Base64 PKCS8)
2. Import it into the `SigningKeys` table
3. Do this close to cutover time

## Cutover Strategy

1. Run user + provider + client migration (can be done days ahead)
2. Seed client configs in Authagonal
3. Import signing key (close to cutover)
4. Optional: migrate active refresh tokens
5. Deploy Authagonal to staging, test
6. Maintenance mode on existing IdentityServer
7. Final migration delta
8. DNS switch (set TTL to 60s beforehand)
9. Monitor 30 minutes
10. If issues: switch DNS back (shared signing key means tokens work on both systems)
