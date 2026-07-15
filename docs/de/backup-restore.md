---
layout: default
title: Backup & Restore
---

# Sicherung & Wiederherstellung

Authagonal stellt zwei CLI-Tools zum Sichern und Wiederherstellen von Azure Table Storage-Daten bereit. Beide sind .NET-Konsolenanwendungen im Verzeichnis `tools/`, und beide sind schlanke Wrapper über das NuGet-Paket `Authagonal.Backup`. Hosts, die geplante, mandantenfähige oder nicht dateisystembasierte Sicherungen benötigen, können die Bibliothek direkt verwenden (siehe [Verwendung der Bibliothek](#using-the-library)).

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
| `--incremental` | Sichert nur Entitäten, die seit der letzten Sicherung geändert wurden |
| `--tables <t1,t2,...>` | Kommagetrennte Liste von Tabellen (Standard: alle Authagonal-Tabellen) |
| `--prefix <prefix>` | Tabellennamen-Präfix (für mandantenfähigen Speicher) |
| `--gzip` | Komprimiert Sicherungsdateien mit gzip (`.jsonl.gz`) |
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
    _tombstones.jsonl.gz
    _manifest.json
```

Jede `.jsonl`-Datei enthält ein JSON-Objekt pro Zeile (eines pro Tabellenentität). Mit `--gzip` werden Dateien als `.jsonl.gz` komprimiert. Die Datei `_manifest.json` zeichnet die Sicherungs-ID, den Zeitstempel, den Modus (`full` oder `incremental`), die Komprimierung, den inkrementellen Wasserstand, die Entitätenanzahl pro Tabelle, die Anzahl der Tombstones, welche Tabellen (falls vorhanden) über das Change-Log gelesen wurden (`ChangeLogTables`, null bedeutet vollständige Scan-Abdeckung), sowie SHA-256-Dateihashes zur Integritätsprüfung auf.

Inkrementelle Sicherungen schreiben zusätzlich eine Datei `_tombstones.jsonl(.gz)`, die Löschungen seit dem Wasserstand aufzeichnet: eine Zeile pro gelöschter Zeile mit `Table`, `PartitionKey`, `RowKey` und `DeletedAt`. Die Wiederherstellung spielt diese erneut ein, damit gelöschte Zeilen nicht wiederauferstehen (siehe [Tombstone-Replay](#tombstone-replay)).

Entitätswerte durchlaufen den Roundtrip exakt: Jede gesicherte Zeile trägt einen Formatmarker `"@v"` sowie eine explizite Annotation `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) für jede Spalte, die JSON nicht eindeutig darstellen kann, sodass die Wiederherstellung die ursprünglichen Typen zurückschreibt statt stringifizierter oder neu erratener Werte.

### Integritätsprüfung

Jedes Sicherungsmanifest enthält ein `FileHashes`-Verzeichnis, das Dateinamen ihren SHA-256-Hashes zuordnet. Bei der Wiederherstellung wird die Integrität jeder Datei anhand dieser Hashes überprüft, bevor deren Daten geschrieben werden; eine Datei, die die Prüfung nicht besteht, oder eine Datendatei, die im Manifest fehlt, bricht die Wiederherstellung mit einem Fehler ab. Sicherungen, die vor der Einführung der Integritätsprüfung erstellt wurden (kein `FileHashes` im Manifest), können nicht verifiziert werden und werden stattdessen mit einer deutlichen Warnung wiederhergestellt. Die Überprüfung kann programmatisch über `RestoreOptions.VerifyIntegrity` deaktiviert werden (Standard `true`).

### Inkrementelle Sicherungen

Übergeben Sie `--incremental`, um nur Entitäten zu sichern, die seit der letzten erfolgreichen Sicherung geändert wurden. Das Tool verwendet die integrierte Eigenschaft `Timestamp` von Azure Table Storage zur Filterung und verfolgt den Höchstwert in einer Datei `.lastbackup` im Ausgabeverzeichnis.

Existiert keine Datei `.lastbackup`, führt der erste inkrementelle Lauf eine vollständige Sicherung durch.

Jeder inkrementelle `Timestamp`-Filter zieht vor der Filterung einen kleinen Sicherheitsspielraum ab (`BackupDefaults.WatermarkSkewMargin`, 5 Minuten). Der Wasserstand stammt von der Uhr des Aufrufers, während Zeilen-Zeitstempel vom Speicherdienst vergeben werden, sodass eine Mutation, die innerhalb dieser Uhrenabweichung committet wird, andernfalls von diesem und jedem späteren Lauf übersehen würde. Das erneute Einlesen des Spielraums kostet pro Lauf einige doppelte Zeilen, die durch die Upsert-Semantik der Wiederherstellung dedupliziert werden.

### Standardtabellen

Das Sicherungstool schließt standardmäßig alle Authagonal-Tabellen ein (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Transiente Tabellen (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) sind standardmäßig ausgeschlossen, da ihre Einträge durch Token-Lebensdauern begrenzt sind; fügen Sie sie bei Bedarf explizit mit `--tables` hinzu. Die Change-Log-Tabelle `Tombstones` wird separat von der Sicherungs-Engine behandelt und sollte nicht aufgeführt werden.

### Signaturschlüssel sind standardmäßig ausgeschlossen

Die Tabelle `SigningKeys` steht zwar in der Standardtabellenliste, wird aber **standardmäßig aus Sicherungen herausgefiltert** (`BackupOptions.IncludeSigningKeys`, Standard `false`; die CLI aktiviert diese Option nie). Bei Hosts, die die lokale (in der Tabelle gespeicherte) Schlüsselquelle verwenden, enthält diese Tabelle den **privaten** JWT-Signaturschlüssel, und ihn in eine Klartext-Sicherungsdatei zu schreiben, würde es jedem, der die Sicherung liest, ermöglichen, Token zu fälschen. (Hosts, die über HashiCorp Vault Transit signieren, halten keinen privaten Schlüssel in der Tabelle, sodass dieses Problem für sie nicht gilt.)

> ⚠️ Aktivieren Sie diese Option nur über `BackupOptions.IncludeSigningKeys`, wenn das Sicherungsziel selbst im Ruhezustand verschlüsselt und zugriffskontrolliert ist. Dasselbe gilt für den Rest der Sicherung: Mit dem standardmäßigen **Klartext**-Geheimnisanbieter enthalten Sicherungen auch Geheimnisse vorgelagerter OIDC-Clients sowie TOTP-/MFA-Seeds im Klartext. Siehe [Konfiguration → Geheimnisanbieter](configuration#secret-provider).

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
| `--prefix <prefix>` | Tabellennamen-Präfix (für mandantenfähigen Speicher) |
| `--dry-run` | Zeigt an, was wiederhergestellt würde, ohne zu schreiben |

### Wiederherstellungsmodi

| Modus | Verhalten |
|---|---|
| `upsert` | Jede Entität einfügen oder ersetzen. Vorhandene Daten werden überschrieben. |
| `merge` | Einfügen oder zusammenführen. Vorhandene Eigenschaften, die nicht in der Sicherung enthalten sind, bleiben erhalten. |
| `clean` | Löscht alle vorhandenen Daten in jeder Tabelle vor der Wiederherstellung. |

Gzip-komprimierte Sicherungsdateien (`.jsonl.gz`) werden automatisch erkannt und dekomprimiert; keine zusätzlichen Flags erforderlich.

### Tombstone-Replay

Nach den Datendateien wendet die Wiederherstellung die `_tombstones`-Datei der Sicherung an: Jeder erfasste Schlüssel wird aus den wiederhergestellten Tabellen gelöscht (`RestoreOptions.ApplyTombstones`, Standard `true`). Die Löschungen eines Inkrements sind ebenso Teil seines Zustands wie seine Upserts; sie zu überspringen würde gelöschte Zeilen, einschließlich DSGVO-gelöschter, wiederauferstehen lassen, wenn eine Abfolge aus vollständiger Sicherung plus Inkrementen wiederhergestellt wird. Vollständige Sicherungen enthalten keine Tombstone-Datei. Wird eine vollständige Sicherung gefolgt von Inkrementen wiederhergestellt, wenden Sie diese in der ältesten zuerst an, damit ein späteres Neuerstellen nach einem früheren Löschen landet. Der Hash der Tombstone-Datei wird wie bei den Datendateien gegen das Manifest verifiziert.

### Exakter Typ-Roundtrip

Zeilen, die mit dem Formatmarker `"@v"` geschrieben wurden, tragen explizite EDM-Typannotationen, sodass die Wiederherstellung die exakten ursprünglichen Spaltentypen rekonstruiert (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); eine nicht annotierte Zeichenkette wird als Zeichenkette wiederhergestellt. Ältere Sicherungsdateien ohne diesen Marker greifen auf eine formbasierte Ableitung zurück, die nur erhalten bleibt, damit alte Sicherungen wiederherstellbar bleiben (die Ableitung kann GUID-förmige oder datumsförmige Zeichenkettenspalten falsch typisieren).

### Exit-Codes

| Code | Bedeutung |
|---|---|
| `0` | Erfolg |
| `1` | Fehler (fehlende Argumente, ungültige Eingabe) |
| `2` | Teilerfolg (einige Entitäten hatten Fehler) |

## Verwendung der Bibliothek

Das NuGet-Paket `Authagonal.Backup` stellt dieselben Vorgänge programmatisch bereit, für Hintergrunddienste oder eine benutzerdefinierte Orchestrierung:

| Typ | Zweck |
|---|---|
| `BackupService` | Führt eine vollständige oder inkrementelle Sicherung gegen einen `TableServiceClient` durch und schreibt in ein `IBackupTarget` |
| `RestoreService` | Verifiziert Hashes und schreibt eine Sicherung zurück in den Table Storage |
| `MergeService` | Streamt eine vollständige Sicherung plus Inkremente (und deren Tombstones) in eine einzige aktuelle Zustandsansicht |
| `RollupService` | Faltet Inkremente in eine neue vollständige Sicherung und löscht dabei optional die Eingaben |
| `BackupOptions` / `RestoreOptions` | Konfiguration pro Lauf |
| `BackupDefaults` | Standardtabellenliste und Change-Log-Voreinstellungen |
| `IBackupSource` / `IBackupTarget` | Speicherabstraktionen; `FileSystemBackupSource` / `FileSystemBackupTarget` sind die eingebauten Implementierungen. Implementieren Sie `IBackupTarget`, um in Blob Storage oder anderswo zu schreiben. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Change-Log-gesteuerte Inkremente

Azure Table Storage indexiert nur `PartitionKey` und `RowKey`, sodass eine nach `Timestamp` gefilterte inkrementelle Sicherung weiterhin einem vollständigen Scan jeder Tabelle entspricht. Um dies zu vermeiden, zeichnen Authagonals Stores jede Mutation in einem Change-Log über die Nahtstelle `IChangeWriter` auf (`Authagonal.Core`), für Azure implementiert durch `TableChangeWriter` (`Authagonal.AzureProvider`). Es handelt sich um eine einzige physische Tabelle, weiterhin `Tombstones` genannt: PK = der logische Tabellenname, RK = `"{pk}|{rk}"`, eine `Op`-Spalte mit `"U"` (Upsert) oder `"D"` (Delete) sowie maßgebliche Spalten `OrigPK`/`OrigRK` (ein `|` innerhalb des ursprünglichen PartitionKey macht das Aufteilen des zusammengesetzten RowKey mehrdeutig, weshalb der Sicherungsleser den Spalten vertraut und nur bei Altzeilen auf die Aufteilung zurückgreift). Jeder Schlüssel hält eine Zeile (Upsert-Replace), sodass die letzte Operation in einem Sicherungsfenster gewinnt.

Wenn der Change-Log-Pfad aktiviert ist, listet eine inkrementelle Sicherung die `Op = "U"`-Change-Log-Einträge einer Tabelle seit dem Wasserstand auf und liest jede lebende Zeile gezielt (Point-Read), statt die Tabelle zu scannen. Die Funktion ist **opt-in und standardmäßig deaktiviert**: `BackupOptions.ChangeLoggedTables` null oder leer bedeutet, dass jede Tabelle auf dem Scan-Pfad bleibt, sodass der Mechanismus inaktiv ausgeliefert wird, bis er bewusst umgeschaltet wird (ein Deploy kann so nicht stillschweigend Zeilen übersehen, die von Code vor der Erfassung geändert wurden). Zwei Voreinstellungen:

| Voreinstellung | Inhalt |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Die Tabellen, deren Schreibvorgänge vollständig im Change-Log erfasst werden |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Dieselbe Menge plus `Users`. Die Login-Status-Schreibvorgänge von Users werden bewusst nicht erfasst (Hot Path, geringer Nutzen), daher ist diese Voreinstellung **nur sicher, wenn Sie zusätzlich den unten beschriebenen vollständigen Scan-Backstop ausführen** |

Die Eigenschaft `ChangeLogTables` des Manifests listet auf, welche Tabellen ein Lauf über das Change-Log gelesen hat; null oder leer bedeutet, dass der Lauf vollständige Scan-Abdeckung hatte (eine vollständige Sicherung, ein einfacher Scan-Inkrement oder ein Backstop-Scan).

### Vollständiger Scan-Backstop

Da die Change-Log-Erfassung Schreibvorgänge übersehen kann (Login-Status-Felder, Writer außerhalb der Stores, Pods, die während eines Deploys noch Code vor der Erfassung ausführen), kombinieren Sie Change-Log-Inkremente mit einem periodischen vollständigen Re-Scan. Setzen Sie `BackupOptions.WatermarkOverride` auf den Zeitstempel des letzten vollständigen Abdeckungs-Scans und lassen Sie `ChangeLoggedTables` für diesen Lauf ungesetzt: Das Inkrement filtert dann über das gesamte Fenster seit diesem Scan nach `Timestamp` und erfasst so alles, was das Change-Log nie erfasst hat. Ein täglicher Backstop neben stündlichen Change-Log-Inkrementen ist ein angemessener Rhythmus. Löschungen sind die einzige Mutationsklasse ohne Selbstheilung (ein Scan lebender Zeilen kann eine Zeile, die verschwunden ist, nicht sehen), weshalb Stores den Lösch-Tombstone schreiben, **bevor** sie die Datenzeile löschen.

Alle inkrementellen Filter, einschließlich des Backstops, ziehen `BackupDefaults.WatermarkSkewMargin` (5 Minuten) vom Wasserstand ab; Aufrufer, die das Change-Log nach einer Sicherung bereinigen, müssen die Bereinigung um denselben Spielraum begrenzen, sonst löschen sie Zeilen, die der nächste Lauf noch benötigt.

### Rollups

`RollupService.RollupAsync` führt eine vollständige Sicherung und ihre Inkremente zu einer neuen vollständigen Sicherung zusammen; `RollupAndCleanAsync` löscht anschließend zusätzlich die Eingaben. Der optionale Parameter `newBackupId` benennt das Ergebnis (null leitet eine Zeitstempel-ID ab); ein speziell aufbewahrter Snapshot (zum Beispiel ein wöchentliches Rollup) muss seine ID hier übergeben, da die ID-basierte Aufbewahrung physische Sicherungs-IDs auflistet, keine Manifeste.

Während eines Merges werden Tombstones nach Zeitstempel-Reihenfolge angewendet: Eine Löschung entfernt eine erfasste Zeile nur dann, wenn deren `Timestamp` nicht nach dem `DeletedAt` des Tombstones liegt. Ein Schlüssel, der früh im Fenster gelöscht und später neu erstellt wurde, hat sowohl einen Tombstone als auch eine lebende Erfassung, und die neu erstellte Zeile übersteht das Rollup. Ältere Tombstones ohne `DeletedAt` entfernen bedingungslos.

## Docker

Das Sicherungstool liefert ein Dockerfile (`tools/Authagonal.Backup/Dockerfile`) für den Einsatz in CI oder ohne installiertes .NET SDK:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

Das Wiederherstellungstool hat kein Image; führen Sie es mit dem .NET SDK aus (`dotnet run --project tools/Authagonal.Restore`).

## Sicherungen planen

Für den Produktionseinsatz führen Sie das Sicherungstool nach einem Zeitplan aus (z. B. täglich vollständig + stündlich inkrementell):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Hosts, die die Bibliothek einbetten, führen typischerweise stündliche Inkremente mit aktiviertem Change-Log-Pfad, einen täglichen vollständigen Scan-Backstop und periodische Rollups aus, um die Inkrement-Kette zu begrenzen.
