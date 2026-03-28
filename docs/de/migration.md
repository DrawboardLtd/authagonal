---
layout: default
title: Migration
locale: de
---

# Migration von Duende IdentityServer

Authagonal enthaelt ein Migrationstool fuer den Umstieg von Duende IdentityServer + SQL Server auf Azure Table Storage.

## Migration ausfuehren

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Oder aus dem Quellcode:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Was migriert wird

| Quelle (SQL Server) | Ziel (Table Storage) | Hinweise |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails | Einzelne JOIN-Abfrage. Claims: given_name, family_name, company, org_id. Passwort-Hashes bleiben unveraendert (BCrypt fuehrt automatisches Upgrade bei Anmeldung durch). |
| `AspNetUserLogins` | UserLogins (Vorwaerts- + Rueckwaerts-Index) | `409 Conflict` = ueberspringen (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV wird in einzelne SSO-Domain-Datensaetze aufgeteilt |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Gleiche Domainaufteilung |
| Duende `Clients` + Untertabellen | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins werden alle in eine einzelne Entitaet zusammengefuehrt |
| Duende `PersistedGrants` (Refresh-Token) | Grants + GrantsBySubject | Opt-in ueber `--MigrateRefreshTokens true`. Nur nicht abgelaufene Token. Bei Auslassung melden sich Benutzer einfach neu an. |

## Optionen

| Option | Standard | Beschreibung |
|---|---|---|
| `--DryRun` | `false` | Protokolliert, was migriert wuerde, ohne in den Speicher zu schreiben |
| `--MigrateRefreshTokens` | `false` | Aktive Refresh-Token einbeziehen. Bei false authentifizieren sich Benutzer nach der Umstellung neu. |

## Idempotenz

Die Migration ist idempotent -- sicher mehrfach ausfuehrbar. Bestehende Datensaetze werden per Upsert aktualisiert (nicht dupliziert). Dies ermoeglicht:

1. Migration Tage vor der Umstellung ausfuehren
2. Eine abschliessende Delta-Migration kurz vor der Umstellung ausfuehren
3. Bei Problemen erneut ausfuehren

## Signaturschluessel-Migration

Noch nicht automatisiert. Um bestehende Token waehrend der Umstellung gueltig zu halten:

1. RSA-Signaturschluessel aus Duende exportieren (typischerweise in appsettings als Base64 PKCS8)
2. In die `SigningKeys`-Tabelle importieren
3. Dies kurz vor der Umstellung durchfuehren

## Umstellungsstrategie

1. Benutzer- + Anbieter- + Client-Migration ausfuehren (kann Tage vorher erfolgen)
2. Client-Konfigurationen in Authagonal initialisieren
3. Signaturschluessel importieren (kurz vor Umstellung)
4. Optional: Aktive Refresh-Token migrieren
5. Authagonal in Staging bereitstellen, testen
6. Bestehenden IdentityServer in Wartungsmodus setzen
7. Abschliessende Delta-Migration
8. DNS-Umstellung (TTL vorher auf 60s setzen)
9. 30 Minuten ueberwachen
10. Bei Problemen: DNS zurueckschalten (gemeinsamer Signaturschluessel bedeutet, dass Token auf beiden Systemen funktionieren)
