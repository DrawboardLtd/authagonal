---
layout: default
title: Backup & Restore
---

# Sicherung & Wiederherstellung

Authagonal stellt zwei CLI-Tools zum Sichern und Wiederherstellen von Azure Table Storage-Daten bereit. Beide sind .NET-Konsolenanwendungen im Verzeichnis `tools/`.

## Sicherung

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Optionen

| Option | Beschreibung |
|---|---|
| `--connection-string <conn>` | Azure Table Storage-Verbindungszeichenfolge (oder die Umgebungsvariable `STORAGE_CONNECTION_STRING` setzen) |
| `--output <dir>` | Ausgabeverzeichnis (Standard: `./backups`) |
| `--incremental` | Nur Entitaeten sichern, die seit der letzten Sicherung geändert wurden |
| `--tables <t1,t2,...>` | Kommagetrennte Liste von Tabellen (Standard: alle Authagonal-Tabellen) |
| `--gzip` | Sicherungsdateien mit gzip komprimieren (`.jsonl.gz`) |
| `--dry-run` | Zeigt an, was gesichert würde, ohne zu schreiben |

### Ausgabeformat

Jede Sicherung erstellt ein Verzeichnis mit Zeitstempel:

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

Jede `.jsonl`-Datei enthält ein JSON-Objekt pro Zeile (eines pro Tabellenentitaet). Mit `--gzip` werden Dateien als `.jsonl.gz` komprimiert. Die Datei `_manifest.json` zeichnet den Sicherungszeitstempel, den Modus, die Komprimierung, die Anzahl der Entitaeten und SHA-256-Dateihashes zur Integritaetspruefung auf.

### Integritaetspruefung

Jedes Sicherungsmanifest enthält ein `FileHashes`-Verzeichnis, das Dateinamen ihren SHA-256-Hashes zuordnet. Während der Wiederherstellung wird die Dateiintegritaet automatisch anhand dieser Hashes verifiziert, bevor Daten geschrieben werden. Wird eine Hash-Abweichung erkannt, bricht die Wiederherstellung mit einem Fehler ab.

### Inkrementelle Sicherungen

Übergeben Sie `--incremental`, um nur Entitaeten zu sichern, die seit der letzten erfolgreichen Sicherung geändert wurden. Das Tool verwendet die integrierte `Timestamp`-Eigenschaft von Azure Table Storage zur Filterung und verfolgt den Hoechstwert in einer `.lastbackup`-Datei im Ausgabeverzeichnis.

Wenn keine `.lastbackup`-Datei existiert, führt der erste inkrementelle Lauf eine vollständige Sicherung durch.

### Standardtabellen

Das Sicherungstool schließt standardmäßig alle Authagonal-Tabellen ein:

`Users`, `UserEmails`, `UserLogins`, `UserExternalIds`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `Roles`

Transiente Tabellen (`SamlReplayCache`, `OidcStateStore`) sind standardmäßig ausgeschlossen — fügen Sie diese bei Bedarf explizit mit `--tables` hinzu.

### Signaturschlüssel sind standardmäßig ausgeschlossen

Die Tabelle `SigningKeys` ist **standardmäßig von Sicherungen ausgeschlossen** (`Backup:IncludeSigningKeys` ist standardmäßig `false`). Bei Hosts, die die lokale (in der Tabelle gespeicherte) Schlüsselquelle verwenden, enthält diese Tabelle den **privaten** JWT-Signaturschlüssel — ihn in eine Klartext-Sicherungsdatei zu schreiben, würde es jedem, der die Sicherung liest, ermöglichen, Token zu faelschen. (Hosts, die über HashiCorp Vault Transit signieren, halten keinen privaten Schlüssel in der Tabelle, sodass dieses Problem für sie nicht gilt.)

> ⚠️ Aktivieren Sie `Backup:IncludeSigningKeys` nur, wenn das Sicherungsziel selbst im Ruhezustand verschlüsselt und zugriffskontrolliert ist. Dasselbe gilt für den Rest der Sicherung: Mit dem standardmäßigen **Klartext**-Geheimnis-Anbieter enthalten Sicherungen auch Geheimnisse von vorgelagerten OIDC-Clients sowie TOTP-/MFA-Seeds im Klartext — siehe [Konfiguration → Geheimnis-Anbieter](configuration#secret-provider).

Bei der Wiederherstellung wird die Dateiintegritaet anhand der SHA-256-Hashes des Manifests verifiziert, bevor Daten geschrieben werden (siehe [Integritaetspruefung](#integritaetspruefung)); eine Hash-Abweichung bricht die Wiederherstellung ab.

## Wiederherstellung

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Optionen

| Option | Beschreibung |
|---|---|
| `--connection-string <conn>` | Azure Table Storage-Verbindungszeichenfolge (oder die Umgebungsvariable `STORAGE_CONNECTION_STRING` setzen) |
| `--input <dir>` | Sicherungsverzeichnis, aus dem wiederhergestellt werden soll |
| `--mode <mode>` | Wiederherstellungsmodus: `upsert` (Standard), `merge` oder `clean` |
| `--tables <t1,t2,...>` | Kommagetrennte Liste der wiederherzustellenden Tabellen (Standard: alle `.jsonl`/`.jsonl.gz`-Dateien in der Sicherung) |
| `--dry-run` | Zeigt an, was wiederhergestellt würde, ohne zu schreiben |

### Wiederherstellungsmodi

| Modus | Verhalten |
|---|---|
| `upsert` | Jede Entitaet einfügen oder ersetzen. Vorhandene Daten werden überschrieben. |
| `merge` | Einfügen oder zusammenfuehren. Vorhandene Eigenschaften, die nicht in der Sicherung enthalten sind, bleiben erhalten. |
| `clean` | Alle vorhandenen Daten in jeder Tabelle vor der Wiederherstellung löschen. |

Mit gzip komprimierte Sicherungsdateien (`.jsonl.gz`) werden automatisch erkannt und dekomprimiert — keine zusätzlichen Flags erforderlich.

### Exit-Codes

| Code | Bedeutung |
|---|---|
| `0` | Erfolg |
| `1` | Fehler (fehlende Argumente, ungültige Eingabe) |
| `2` | Teilerfolg (einige Entitaeten hatten Fehler) |

## Docker

Für beide Tools sind Docker-Images verfügbar, um sie in CI oder ohne installiertes .NET SDK auszufuehren:

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

## Sicherungen planen

Für den Produktionseinsatz fuehren Sie das Sicherungstool nach einem Zeitplan aus (z. B. täglich vollständig + stuendlich inkrementell):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
