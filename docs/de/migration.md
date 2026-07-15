---
layout: default
title: Migration
locale: de
---

# Migration von Duende IdentityServer

Authagonal enthält ein Migrationstool für den Umstieg von Duende IdentityServer + SQL Server auf Azure Table Storage.

## Migration ausführen

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(Kein `--`-Trennzeichen nach dem Image-Namen: Alles danach wird direkt an das Tool übergeben, und ein einzelnes `--` bricht die Optionsverarbeitung.)

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
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + Namensindizes | Einzelne JOIN-Abfrage. Claims: given_name, family_name, company, org_id (Typen überschreibbar, siehe unten). Passwort-Hashes bleiben unverändert erhalten; ASP.NET-Identity-V3- und BCrypt-Hashes werden unverändert verifiziert und bei der nächsten erfolgreichen Anmeldung auf Authagonals natives PBKDF2-Format aktualisiert. |
| `AspNetUserLogins` | UserLogins (Vorwärts- + Rückwärtsindex) | `409 Conflict` = überspringen (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains`-CSV wird in einzelne SSO-Domänendatensätze aufgeteilt |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Gleiche Domänenaufteilung |
| Duende `Clients` + untergeordnete Tabellen | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins werden alle in einer einzigen Entität zusammengeführt |
| Duende `PersistedGrants` (Refresh Token) | Grants + GrantsBySubject + GrantsByExpiry | Opt-in über `--MigrateRefreshTokens true`. Nur nicht abgelaufene Token. Wird dies ausgelassen, melden sich Benutzer einfach neu an. |

## Optionen

| Option | Standard | Beschreibung |
|---|---|---|
| `--DryRun` | `false` | Protokolliert, was migriert würde, ohne in den Speicher zu schreiben |
| `--MigrateRefreshTokens` | `false` | Aktive Refresh Token einbeziehen. Bei `false` authentifizieren sich Benutzer nach der Umstellung neu. |
| `--Source:ClaimMap:{claim}` | der OIDC-Claim-Name selbst | Überschreibt den gelesenen `AspNetUserClaims`-ClaimType für einen zugeordneten Claim, z. B. `--Source:ClaimMap:given_name=FirstName`. Wird verwendet für `given_name`, `family_name`, `company`, `org_id`. |

## Idempotenz

Die Migration ist idempotent und kann gefahrlos mehrfach ausgeführt werden. Bestehende Datensätze werden aktualisiert oder übersprungen, niemals dupliziert. Dies ermöglicht Ihnen:

1. Die Migration Tage vor der Umstellung auszuführen
2. Eine abschließende Delta-Migration kurz vor der Umstellung auszuführen
3. Bei Problemen erneut auszuführen

## Was NICHT migriert wird

Diese Authagonal-Funktionen haben kein Duende-Äquivalent und sind nach der Migration leer:

- **Rollen**: RBAC-Rollen und Benutzer-Rollen-Zuweisungen
- **MFA-Anmeldedaten**: TOTP-, WebAuthn- und Wiederherstellungscode-Registrierungen
- **SCIM-Token und -Gruppen**: SCIM-Bereitstellungskonfiguration
- **Benutzerbereitstellungen**: TCC-Status der nachgelagerten App-Bereitstellung

Benutzer müssen MFA erneut registrieren, wenn die `MfaPolicy` Ihres Clients auf `Enabled` oder `Required` gesetzt ist.

## Signaturschlüssel-Migration

Noch nicht automatisiert. So bleiben bestehende Token über die Umstellung hinweg gültig:

1. Den RSA-Signaturschlüssel aus Duende exportieren (typischerweise in appsettings als Base64 PKCS8)
2. In die Tabelle `SigningKeys` importieren
3. Dies kurz vor dem Umstellungszeitpunkt durchführen

## Umstellungsstrategie

1. Benutzer- + Anbieter- + Client-Migration ausführen (kann Tage vorher erfolgen)
2. Client-Konfigurationen in Authagonal anlegen
3. Signaturschlüssel importieren (kurz vor der Umstellung)
4. Optional: aktive Refresh Token migrieren
5. Authagonal in der Staging-Umgebung bereitstellen und testen
6. Wartungsmodus für den bestehenden IdentityServer aktivieren
7. Abschließende Delta-Migration
8. DNS-Umstellung (TTL vorher auf 60s setzen)
9. 30 Minuten überwachen
10. Bei Problemen: DNS zurückschalten (ein gemeinsamer Signaturschlüssel bedeutet, dass Token auf beiden Systemen funktionieren)
