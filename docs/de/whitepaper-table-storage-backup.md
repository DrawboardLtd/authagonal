---
layout: default
title: Table Storage Backup Whitepaper
locale: de
---

# Sicherung von Azure Table Storage: Ein praktischer Ansatz

**Wie Authagonal vollständige und inkrementelle Sicherungen für einen schemalosen NoSQL-Speicher implementiert**

---

## Das Problem

Azure Table Storage ist ein kostengünstiger, massiv skalierbarer Key-Value-Speicher, bietet jedoch keine native Sicherungsfunktion. Es gibt keine Snapshots, keine Point-in-Time-Wiederherstellung, keinen Export-Button. Wenn ein fehlerhaftes Deployment Daten beschädigt oder ein Operator versehentlich eine Tabelle löscht, hängt die Wiederherstellung vollständig davon ab, was Sie selbst gebaut haben.

Für eine Identitätsplattform wie Authagonal, deren Tabellen Benutzer, Anmeldeinformationen, OAuth-Grants, Signaturschlüssel, SSO-Konfigurationen und SCIM-Bereitstellungsstatus enthalten, steht viel auf dem Spiel. Der Verlust dieser Daten legt nicht nur eine Anwendung lahm, sondern sperrt Menschen aus.

Dieses Paper beschreibt die Sicherungsstrategie von Authagonal: wie Daten exportiert werden, wie inkrementelle Sicherungen trotz des eingeschränkten Abfragemodells von Table Storage funktionieren, wie Löschungen nachverfolgt werden und wie die einzelnen Teile zu einer produktionsreifen Sicherungs-Pipeline zusammenwirken.

## Designziele

1. **Vollständige und inkrementelle Sicherungen.** Eine tägliche vollständige Sicherung genügt für kleine Deployments, aber in großem Maßstab halten stündliche Inkremente das Sicherungsfenster kurz und die Speicherkosten niedrig.
2. **Verlustfreier Round-Trip.** Jede Entitätseigenschaft, Strings, Ganzzahlen, Booleans, DateTimeOffsets, GUIDs, Binärdaten, muss einen Sicherungs-/Wiederherstellungszyklus ohne Typumwandlung oder Datenverlust überstehen.
3. **Mandantenfähigkeit (Multi-Tenant).** Authagonal nutzt Tabellennamen-Präfixe, um Mandanten zu isolieren (z. B. `acmecorpUsers`, `acmecorpClients`). Sicherung und Wiederherstellung müssen präfixbewusst sein, damit ein einzelnes Speicherkonto viele Mandanten mit unabhängigen Sicherungsplänen hosten kann.
4. **Austauschbarer Speicher.** Sicherungen sollten während der Entwicklung auf ein lokales Dateisystem und in der Produktion auf Blob Storage (oder ein beliebiges anderes Ziel) funktionieren, ohne die Kernlogik zu ändern.
5. **Menschenlesbare Ausgabe.** Wenn etwas schiefgeht, sollte ein Operator eine Sicherungsdatei in einem Texteditor öffnen und sehen können, was darin enthalten ist.

## Architektur

Das Sicherungssystem ist als .NET-Bibliothek (`Authagonal.Backup`) mit schlanken CLI-Wrappern für Sicherungs- und Wiederherstellungsvorgänge aufgebaut. Die Bibliothek ist vom Hauptserver von Authagonal getrennt, sodass sie als eigenständiges Tool, in einem Docker-Container oder eingebettet in einen geplanten Job verwendet werden kann.

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

### Speicherabstraktion

Die Kerndienste greifen niemals direkt auf das Dateisystem zu. Sie arbeiten gegen zwei Schnittstellen:

**IBackupTarget** stellt vier Operationen bereit: das Öffnen eines beschreibbaren Streams für eine Sicherungsdatei, das Schreiben eines Manifests, das Abrufen des letzten Wasserzeichens (für die inkrementelle Planung) und das Setzen eines neuen Wasserzeichens.

**IBackupSource** stellt die Leseseite bereit: das Lesen eines Manifests, das Öffnen eines lesbaren Streams, das chronologische Auflisten von Sicherungs-IDs, das Auflisten von Dateien innerhalb einer Sicherung und das Löschen einer Sicherung.

Die Dateisystem-Implementierungen sind unkompliziert, zeitgestempelte Verzeichnisse mit JSONL-Dateien darin, aber die Abstraktion bedeutet, dass der Wechsel zu Azure Blob Storage oder S3 lediglich die Implementierung dieser beiden Schnittstellen erfordert.

## Vollständige Sicherung

Eine vollständige Sicherung iteriert über jede Authagonal-Tabelle, fragt alle Entitäten ab und schreibt sie in JSONL-Dateien (ein JSON-Objekt pro Zeile, eine Datei pro Tabelle).

Der Sicherungsprozess:

1. Erzeugen einer Sicherungs-ID aus dem aktuellen UTC-Zeitstempel (z. B. `20260329-120000`).
2. Für jede der 20 Standardtabellen von Authagonal wird das SDK von Azure Table Storage mit `QueryAsync<TableEntity>` bei einer Seitengröße von 1.000 abgefragt.
3. Serialisieren jeder Entität in ein flaches JSON-Dictionary unter Beibehaltung aller Eigenschaften, einschließlich der Systemeigenschaften (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Schreiben jeder serialisierten Entität als einzelne Zeile in `{TableName}.jsonl` (oder `{TableName}.jsonl.gz`, wenn Komprimierung aktiviert ist).
5. Erfassen der Entitätsanzahl und Dauer pro Tabelle in einem Manifest (`_manifest.json`).
6. Aktualisieren der Wasserzeichendatei `.lastbackup` mit dem Startzeitpunkt der Sicherung.

Tabellen, die im Speicherkonto nicht existieren, werden stillschweigend übersprungen (HTTP 404 wird abgefangen und ignoriert). Transiente Tabellen wie `SamlReplayCache` und `OidcStateStore` werden standardmäßig ausgeschlossen, da ihr Inhalt flüchtig ist.

### Ausgabeformat

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

Eine einzelne Zeile in `Users.jsonl` sieht so aus:

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

JSONL wurde gegenüber CSV oder einem Binärformat bevorzugt, weil es die schemalose, heterogene Natur von Table-Storage-Entitäten bewahrt (unterschiedliche Entitäten in derselben Tabelle können unterschiedliche Eigenschaften haben), streambar ist (die gesamte Tabelle muss nicht im Speicher gepuffert werden) und mit Standardwerkzeugen wie `jq` oder einem beliebigen Texteditor direkt einsehbar ist.

### Komprimierung

Wenn das Flag `--gzip` gesetzt ist, wird jede JSONL-Datei vor dem Schreiben in einen GZip-Stream mit `CompressionLevel.Optimal` verpackt. Die Dateiendung ändert sich zu `.jsonl.gz`. Das Wiederherstellungstool erkennt GZip automatisch, indem es die Magic Bytes (`0x1f 0x8b`) am Anfang jeder Datei prüft, sodass bei der Wiederherstellung kein Flag erforderlich ist.

## Inkrementelle Sicherung

### Der Timestamp-Trick

Azure Table Storage pflegt automatisch eine `Timestamp`-Eigenschaft bei jeder Entität, die bei jedem Einfügen oder Ersetzen aktualisiert wird. Dies ist eine serverseitig verwaltete Eigenschaft, Anwendungen können sie nicht selbst setzen. Das Sicherungssystem nutzt dies, indem es Abfragen auf `Timestamp gt datetime'{watermark}'` filtert, wobei das Wasserzeichen der Startzeitpunkt der letzten erfolgreichen Sicherung ist.

Das bedeutet, eine inkrementelle Sicherung lädt nur Entitäten herunter, die seit dem letzten Lauf erstellt oder geändert wurden. Bei einem System mit 500.000 Entitäten, von denen 200 in der letzten Stunde geändert wurden, überträgt die inkrementelle Sicherung 200 Zeilen statt 500.000.

Das Wasserzeichen wird in einer Datei `.lastbackup` im Wurzelverzeichnis der Sicherung gespeichert. Existiert die Datei nicht (erster Lauf oder nach manueller Bereinigung), greift die Sicherung auf einen vollständigen Export zurück. Inkrementelle Sicherungs-IDs enthalten ein Suffix `-incr` (z. B. `20260329-180000-incr`), und das Manifest verzeichnet `"mode": "incremental"` zusammen mit dem für die Filterung verwendeten Wasserzeichenwert.

### Kosten des Timestamp-Filters

Es lohnt sich, ehrlich über eine Einschränkung zu sein: `Timestamp` ist nicht indiziert. Azure Table Storage indiziert nur `PartitionKey` und `RowKey`. Ein Filter auf `Timestamp gt datetime'...'` führt zu einem vollständigen Tabellenscan, Azure liest serverseitig jede Entität und wertet das Prädikat aus, bevor Treffer zurückgegeben werden. Die Filterung reduziert die Datenübertragung (nur geänderte Entitäten werden übertragen), nicht aber die serverseitigen Lesekosten.

Wichtiger noch: Der aktuelle Ansatz scannt **alle 20 Tabellen** einzeln, selbst wenn nur eine Tabelle Änderungen hatte. Das sind 20 vollständige Tabellenscans pro inkrementeller Sicherung, unabhängig davon, wie wenige Entitäten sich tatsächlich geändert haben.

Bei den für Authagonal typischen Datenvolumina im Identitätsbereich (Zehntausende Entitäten, nicht Millionen) ist dies vollkommen akzeptabel: Scans sind schnell, Lesevorgänge sind günstig (0,00036 $ pro 10.000 Transaktionen), und der Vorgang ist rein lesend, ohne Auswirkung auf den Live-Traffic. Der Abschnitt zum [Skalieren über Timestamp-Scans hinaus](#scaling-beyond-timestamp-scans) beschreibt, wie sich dies weiterentwickeln könnte.

### Das Löschproblem

Der `Timestamp`-Filter erfasst Einfügungen und Aktualisierungen elegant, kann jedoch keine Löschungen erfassen. Eine gelöschte Entität verschwindet einfach, es gibt keinen `Timestamp`, nach dem gefiltert werden könnte, kein Tombstone, das von Table Storage selbst hinterlassen wird.

Authagonal löst dies mit anwendungsseitiger Tombstone-Nachverfolgung.

## Tombstone-Nachverfolgung

Jeder Datenspeicher in Authagonal (Benutzer, Clients, Grants, Signaturschlüssel, SSO-Domänen, SAML/OIDC-Provider, MFA-Anmeldeinformationen, SCIM-Ressourcen, Rollen) akzeptiert eine optionale `ITombstoneWriter`-Abhängigkeit. Wenn ein Store eine Entität löscht, schreibt er einen Tombstone-Datensatz in eine dedizierte Tabelle `Tombstones`:

| Spalte | Wert |
|---|---|
| `PartitionKey` | Logischer Tabellenname (z. B. `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | UTC-Zeitstempel der Löschung |

Dies ist ein leichtgewichtiger, überwiegend anfügender Seitenkanal. Das Schreiben des Tombstones ist ein einfacher Upsert, in Batches bis zum 100-Entitäten-Transaktionslimit von Azure für Massenoperationen gebündelt.

Während einer inkrementellen Sicherung fragt der Sicherungsdienst nach dem Export der geänderten Entitäten aus jeder Tabelle die Tabelle `Tombstones` nach Datensätzen mit `Timestamp > watermark` ab. Diese werden in eine separate Datei `_tombstones.jsonl` im Sicherungsverzeichnis geschrieben, mit einem normalisierten Format:

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

Das bedeutet, eine inkrementelle Sicherung erfasst ein vollständiges Bild der Änderungen: hinzugefügte/geänderte Entitäten (aus den JSONL-Dateien pro Tabelle) und gelöschte Entitäten (aus der Tombstone-Datei).

## Zusammenführung und Rollup

Im Laufe der Zeit sammelt ein Sicherungsverzeichnis eine vollständige Sicherung und viele Inkremente an. Um den aktuellen Zustand wiederherzustellen, müssten alle der Reihe nach angewendet werden. Der **MergeService** konsolidiert sie zu einer einzigen vollständigen Sicherung.

Der Zusammenführungsalgorithmus:

1. Laden der Entitätsmenge der vollständigen Sicherung, jeweils eine Tabelle zur Zeit (um den Speicherverbrauch zu begrenzen).
2. Schichten jedes Inkrements darüber in chronologischer Reihenfolge, neuere Werte überschreiben ältere, mit dem Schlüssel `(PartitionKey, RowKey)`.
3. Anwenden der Tombstones: für jedes `(Table, PartitionKey, RowKey)`-Tupel in den Tombstone-Dateien wird die Entität aus der zusammengeführten Menge entfernt.
4. Schreiben der resultierenden Entitätsmenge als neue vollständige Sicherung.

Der **RollupService** umschließt dies mit einer Bereinigung: Nach einer erfolgreichen Zusammenführung löscht er die alte vollständige Sicherung sowie alle Inkremente, die eingearbeitet wurden. Dies verhindert, dass der Speicherverbrauch unbegrenzt wächst.

Ein typischer Produktionsplan könnte so aussehen:

- **Stündlich:** Inkrementelle Sicherung
- **Täglich (2 Uhr):** Vollständige Sicherung
- **Wöchentlich:** Rollup (Zusammenführen der täglichen + stündlichen Inkremente der vorherigen Woche, Löschen der Originale)

## Wiederherstellung

Das Wiederherstellungstool liest ein Sicherungsverzeichnis und schreibt Entitäten zurück in Azure Table Storage. Es unterstützt drei Modi:

**Upsert** (Standard): Jede Entität wird eingefügt oder ersetzt. Vorhandene Entitäten mit demselben Schlüssel werden überschrieben. Dies ist der sicherste Modus für die Notfallwiederherstellung (Disaster Recovery).

**Merge**: Jede Entität wird eingefügt oder zusammengeführt. Im Backup vorhandene Eigenschaften überschreiben die entsprechenden Eigenschaften der vorhandenen Entität, aber Eigenschaften, die in der Live-Tabelle, aber nicht im Backup existieren, bleiben erhalten. Nützlich für partielle Wiederherstellungen.

**Clean**: Alle vorhandenen Entitäten in jeder Zieltabelle werden vor der Wiederherstellung gelöscht. Dies erzeugt eine exakte Kopie des Sicherungszustands, auf Kosten eines (möglicherweise langsamen) vollständigen Tabellenscans zum Löschen der vorhandenen Daten.

### Typtreue

Eine zentrale Herausforderung beim Round-Trip von Table-Storage-Daten durch JSON besteht darin, die Eigenschaftstypen zu bewahren. Table Storage unterstützt nativ Strings, Ganzzahlen (Int32/Int64), Fließkommazahlen, Booleans, DateTimeOffset, Guid und Binärdaten. JSON hat für die meisten davon keine native Repräsentation.

Der Wiederherstellungsdienst verwendet Heuristiken, um Typen aus ihrer JSON-String-Darstellung wiederherzustellen:

- **DateTimeOffset**: Strings, die 19-35 Zeichen lang sind, mit einer Ziffer beginnen und sich als ISO 8601 parsen lassen, werden als `DateTimeOffset` wiederhergestellt.
- **Guid**: Strings, die genau 36 Zeichen lang sind und sich als GUID parsen lassen, werden als `Guid` wiederhergestellt.
- **Zahlen**: JSON-Zahlen werden der Reihe nach als `Int32`, dann `Int64`, dann `double` versucht.
- **Booleans und Nullwerte**: Werden direkt abgebildet.

Dieser heuristische Ansatz deckt die tatsächlichen Datenmuster von Authagonal ab, ohne eine Schemaregistrierung oder Typannotationen im Sicherungsformat zu erfordern.

### Fehlerbehandlung

Wiederherstellungsvorgänge sind auf Entitätsebene fehlertolerant. Wenn das Schreiben einer einzelnen Entität fehlschlägt (z. B. aufgrund eines transienten Azure-Fehlers), wird der Fehlerzähler erhöht, aber die Wiederherstellung wird fortgesetzt. Das Endergebnis meldet Erfolgs- und Fehlerzahlen pro Tabelle, und der Prozess wird mit dem Code `2` für teilweisen Erfolg beendet, im Unterschied zu `0` (vollständiger Erfolg) und `1` (fataler Fehler).

## Mandantenfähigkeit

Authagonal unterstützt mandantenfähige Deployments, bei denen die Tabellen jedes Mandanten mit einem Präfix versehen sind (z. B. `acmecorpUsers`, `contosoclients`). Sowohl Sicherung als auch Wiederherstellung akzeptieren ein Flag `--prefix`, das logischen Tabellennamen vorangestellt wird, wenn mit Azure Table Storage kommuniziert wird.

Das bedeutet:
- Eine Sicherung mit `--prefix acmecorp` liest aus `acmecorpUsers`, `acmecorpClients` usw., schreibt aber Dateien mit den Namen `Users.jsonl`, `Clients.jsonl` (logische Namen).
- Eine Wiederherstellung mit `--prefix contoso` liest `Users.jsonl` und schreibt nach `contosoUsers`.

Dies macht es unkompliziert, die Daten eines Mandanten zu klonen, zwischen Umgebungen zu migrieren oder einen Mandanten wiederherzustellen, ohne andere zu beeinträchtigen.

## Manifest

Jede Sicherung enthält eine Datei `_manifest.json`, die Folgendes verzeichnet:

- **BackupId**: Zeitgestempelte Kennung (z. B. `20260329-120000` oder `20260329-180000-incr`)
- **Mode**: `"full"` oder `"incremental"`
- **BackupTimestamp**: Wann die Sicherung begonnen hat (UTC)
- **Watermark**: Bei Inkrementen der für die Filterung verwendete Grenzzeitstempel
- **Compressed**: Ob die Dateien GZip-komprimiert sind
- **Tables**: Ein Dictionary von Tabellennamen zu Entitätsanzahlen und Dauern
- **TombstoneCount**: Anzahl der Tombstone-Datensätze (nur bei Inkrementen)
- **TotalEntities**: Aggregierte Entitätsanzahl über alle Tabellen
- **DurationSeconds**: Wall-Clock-Zeit für den Sicherungslauf
- **FileHashes**: SHA-256-Hashes jeder Sicherungsdatei zur Integritätsprüfung

Das Manifest dient sowohl als operatives Dashboard (wie groß war die Sicherung? wie lange hat sie gedauert? welche Tabellen sind am größten?) als auch als Sicherheitsnetz (die Hash-Überprüfung bei der Wiederherstellung erkennt beschädigte oder manipulierte Dateien).

## Operative Merkmale

**Sicherungsgeschwindigkeit** wird durch den Abfragedurchsatz von Azure Table Storage begrenzt, der typischerweise bei 5.000-10.000 Entitäten pro Sekunde und Tabelle liegt. Eine vollständige Sicherung von 100.000 Entitäten über 20 Tabellen wird in unter einer Minute abgeschlossen. Inkrementelle Sicherungen von einigen hundert geänderten Entitäten sind in Sekunden erledigt.

**Speicherverbrauch** ist minimal. Der Sicherungsdienst streamt Entitäten direkt auf die Festplatte, er lädt niemals eine ganze Tabelle in den Speicher. Der Merge-Dienst verarbeitet jeweils eine Tabelle zur Zeit und lädt nur die Entitätsmenge dieser Tabelle. Bei sehr großen Tabellen (Millionen von Entitäten) ist der Speicherbedarf der Zusammenführung proportional zur größten einzelnen Tabelle.

**Wiederholungsrichtlinie**: konfiguriert mit exponentiellem Backoff: 5 Wiederholungen, beginnend bei 500 ms, gedeckelt bei 30 Sekunden. Dies deckt die transiente Drosselung ab, die Table Storage unter hoher Last anwendet.

**Testlauf**-Modus (`--dry-run`) zählt Entitäten auf, ohne Dateien zu schreiben, nützlich zur Überprüfung der Konnektivität und zur Schätzung der Sicherungsgröße, bevor man sich auf einen vollständigen Lauf festlegt.

## Skalierung über Timestamp-Scans hinaus

Der auf `Timestamp` basierende Ansatz ist bei moderatem Umfang pragmatisch, aber seine Kosten sind proportional zur gesamten Datenmenge, nicht zur Anzahl der Änderungen. Mit wachsenden Tabellen werden 20 vollständige Tabellenscans pro inkrementeller Sicherung zunehmend verschwenderisch. Die natürliche Weiterentwicklung ist eine **einheitliche Änderungsprotokolltabelle** (Change-Log-Tabelle).

Die Erkenntnis ist, dass der Tombstone-Mechanismus dieses Muster für Löschungen bereits beweist. Die Tabelle `Tombstones` ist ein einziger, kompakter, tabellenübergreifender Index: Jede Löschung über alle 20 Datentabellen hinweg wird an einer Stelle erfasst, abfragbar nach Zeitstempel. Dies auf alle Mutationen auszuweiten, Einfügungen, Aktualisierungen und Löschungen, würde die Notwendigkeit, die Datentabellen zu scannen, vollständig beseitigen.

### Change-Log-Design

Eine Änderungsprotokolltabelle mit zeitlich gebündelten Partitionsschlüsseln (time-bucketed) würde so aussehen:

| PartitionKey | RowKey | Properties |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

Der Partitionsschlüssel ist ein Stunden-Bucket, sodass das Auffinden aller Änderungen seit der letzten Sicherung zu einer Reihe von **Partitionsschlüssel-Punktabfragen** wird, der schnellsten Operation, die Table Storage unterstützt. Der Sicherungsdienst würde:

1. Das Änderungsprotokoll für alle Stunden-Bucket-Partitionen seit dem Wasserzeichen abfragen. Dies ist eine indizierte Operation, kein Scan.
2. Für jeden `upsert`-Eintrag die aktuelle Entität aus der Datentabelle anhand ihres exakten `PartitionKey`/`RowKey` abrufen, ebenfalls ein indizierter Punktzugriff.
3. Für jeden `delete`-Eintrag das Tombstone direkt aus dem Änderungsprotokoll erfassen. Keine separate Tombstones-Tabelle nötig.

Dies macht die Sicherungskosten proportional zur Anzahl der Änderungen, nicht zur gesamten Datenmenge. Eine Abfrage gegen eine kompakte Indextabelle ersetzt 20 vollständige Tabellenscans. Es vereinheitlicht zudem den Tombstone-Mechanismus, das Änderungsprotokoll erfasst Erstellungen, Aktualisierungen und Löschungen einheitlich, sodass die separate Tabelle `Tombstones` überflüssig wird.

### Warum noch nicht

Der Kompromiss liegt im Overhead auf dem Schreibpfad. Jede Mutation in jedem Store bräuchte ein zusätzliches Schreiben in die Änderungsprotokolltabelle. Die Verdrahtung ist größtenteils schon vorhanden, der `ITombstoneWriter` ist bereits in jeden Store injiziert und wird bei jeder Löschung aufgerufen. Ihn zu einem `IChangeTracker` zu erweitern, der auch bei Upserts auslöst, wäre ein unkompliziertes Refactoring.

Aber "unkompliziert" heißt nicht "kostenlos". Es fügt jeder benutzerseitigen Operation Latenz hinzu (ein zusätzliches Schreiben in Table Storage), erhöht die Speichertransaktionen und führt ein neues Konsistenzproblem ein (was, wenn das Schreiben der Daten gelingt, aber das Schreiben des Änderungsprotokolls fehlschlägt?). Bei den aktuellen Volumina sind die 20 zeitstempelgefilterten Scans in Sekunden abgeschlossen und kosten Bruchteile eines Cents. Das Änderungsprotokoll wäre der richtige Schritt, wenn die Tabellen auf Millionen von Entitäten anwachsen würden, aber vorerst gewinnt der einfachere Ansatz.

## Zusammenfassung

Der Ansatz ist bewusst einfach gehalten. Anstatt eine komplexe Change-Data-Capture-Pipeline aufzubauen oder sich auf Azure-spezifische Funktionen zu verlassen, die für Table Storage möglicherweise nicht existieren, nutzt Authagonal das eine Metadatum, das Azure *tatsächlich* garantiert, den serverseitig verwalteten `Timestamp`, kombiniert mit anwendungsseitiger Tombstone-Nachverfolgung für Löschungen.

Das Ergebnis ist ein Sicherungssystem, das:

- Menschenlesbare, portable JSONL-Dateien erzeugt
- Vollständige und inkrementelle Modi mit automatischer Wasserzeichenverwaltung unterstützt
- Erstellungen, Aktualisierungen *und* Löschungen korrekt erfasst
- Mandantenübergreifende Tabellenpräfixe transparent handhabt
- Sich sauber zusammensetzen lässt (Merge, Rollup, selektive Wiederherstellung)
- Als eigenständiges Tool ohne Abhängigkeit vom Authagonal-Server läuft

Die Speicherabstraktion bedeutet, dass dieselbe Logik lokale Festplatten, Azure Blob Storage, S3 oder ein beliebiges anderes Ziel ansprechen kann. Das Format ist einfach genug, dass ein Operator auch ohne das Wiederherstellungstool Daten mit `jq` und der Azure CLI rekonstruieren könnte.
