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
| `--incremental` | Nur Entitaeten sichern, die seit der letzten Sicherung geaendert wurden |
| `--tables <t1,t2,...>` | Kommagetrennte Liste von Tabellen (Standard: alle Authagonal-Tabellen) |
| `--dry-run` | Zeigt an, was gesichert wuerde, ohne zu schreiben |

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
  20260329-180000-incr/     (incremental backup)
    Users.jsonl
    _manifest.json
```

Jede `.jsonl`-Datei enthaelt ein JSON-Objekt pro Zeile (eines pro Tabellenentitaet). Die Datei `_manifest.json` zeichnet den Sicherungszeitstempel, den Modus und die Anzahl der Entitaeten auf.

### Inkrementelle Sicherungen

Uebergeben Sie `--incremental`, um nur Entitaeten zu sichern, die seit der letzten erfolgreichen Sicherung geaendert wurden. Das Tool verwendet die integrierte `Timestamp`-Eigenschaft von Azure Table Storage zur Filterung und verfolgt den Hoechstwert in einer `.lastbackup`-Datei im Ausgabeverzeichnis.

Wenn keine `.lastbackup`-Datei existiert, fuehrt der erste inkrementelle Lauf eine vollstaendige Sicherung durch.

### Standardtabellen

Das Sicherungstool schliesst standardmaessig alle Authagonal-Tabellen ein:

`Users`, `UserEmails`, `UserLogins`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`

Transiente Tabellen (`SamlReplayCache`, `OidcStateStore`) sind standardmaessig ausgeschlossen — fuegen Sie diese bei Bedarf explizit mit `--tables` hinzu.

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
| `--tables <t1,t2,...>` | Kommagetrennte Liste der wiederherzustellenden Tabellen (Standard: alle `.jsonl`-Dateien in der Sicherung) |
| `--dry-run` | Zeigt an, was wiederhergestellt wuerde, ohne zu schreiben |

### Wiederherstellungsmodi

| Modus | Verhalten |
|---|---|
| `upsert` | Jede Entitaet einfuegen oder ersetzen. Vorhandene Daten werden ueberschrieben. |
| `merge` | Einfuegen oder zusammenfuehren. Vorhandene Eigenschaften, die nicht in der Sicherung enthalten sind, bleiben erhalten. |
| `clean` | Alle vorhandenen Daten in jeder Tabelle vor der Wiederherstellung loeschen. |

### Exit-Codes

| Code | Bedeutung |
|---|---|
| `0` | Erfolg |
| `1` | Fehler (fehlende Argumente, ungueltige Eingabe) |
| `2` | Teilerfolg (einige Entitaeten hatten Fehler) |

## Docker

Fuer beide Tools sind Docker-Images verfuegbar, um sie in CI oder ohne installiertes .NET SDK auszufuehren:

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

Fuer den Produktionseinsatz fuehren Sie das Sicherungstool nach einem Zeitplan aus (z. B. taeglich vollstaendig + stuendlich inkrementell):

```bash
# Daily full backup
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups

# Hourly incremental
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental
```
