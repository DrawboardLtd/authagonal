---
layout: default
title: Skalierung
locale: de
---

# Skalierung

Authagonal ist so konzipiert, dass es ohne spezielle Konfiguration sowohl vertikal als auch horizontal skaliert werden kann.

## Zustandslos durch Design

Alle persistenten Zustaende werden in Azure Table Storage gespeichert. Es gibt keinen In-Process-Zustand, der Sticky Sessions oder Koordination zwischen Instanzen erfordert:

- **Signaturschluessel** — aus Table Storage geladen, stuendlich aktualisiert
- **Autorisierungscodes und Refresh-Tokens** — in Table Storage mit Einmalverwendung gespeichert
- **SAML Replay-Schutz** — Anfrage-IDs werden in Table Storage mit atomarem Loeschen verfolgt
- **OIDC State und PKCE Verifier** — in Table Storage gespeichert
- **Client- und Provider-Konfiguration** — pro Anfrage aus Table Storage abgerufen

## Cookie-Verschluesselung (Data Protection)

Die Data Protection Schluessel von ASP.NET Core werden automatisch in Azure Blob Storage persistiert, wenn eine echte Azure Storage Verbindungszeichenfolge verwendet wird. Das bedeutet, dass Cookies, die von einer Instanz signiert wurden, von jeder anderen Instanz entschluesselt werden koennen — keine Sticky Sessions erforderlich.

Fuer die lokale Entwicklung mit Azurite fallen Data Protection Schluessel auf den standardmaessigen dateibasierten Speicher zurueck.

Sie koennen auch eine explizite Blob-URI ueber die Konfiguration angeben:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Caches pro Instanz

Eine kleine Anzahl von haeufig gelesenen, sich langsam aendernden Werten wird pro Instanz im Speicher zwischengespeichert, um Table Storage Roundtrips zu reduzieren:

| Daten | Cache-Dauer | Auswirkung bei Veralterung |
|---|---|---|
| OIDC Discovery-Dokumente | 60 Minuten | Verzoegerte Erkennung von IdP-Schluesselrotation |
| SAML IdP-Metadaten | 60 Minuten | Gleich |
| CORS erlaubte Origins | 60 Minuten | Neue Origins benoetigen bis zu einer Stunde zur Verbreitung |

Diese Caches sind fuer den Produktionseinsatz akzeptabel. Wenn Sie eine sofortige Verbreitung benoetigen, starten Sie die betroffenen Instanzen neu.

## Ratenbegrenzung

Authagonal enthaelt keine integrierte Ratenbegrenzung. Die Ratenbegrenzung sollte auf der Infrastrukturebene (Load Balancer, API-Gateway oder Reverse Proxy) angewendet werden, wo eine einheitliche Sicht auf den gesamten Datenverkehr aller Instanzen besteht.

## Skalierungsempfehlungen

**Vertikale Skalierung** — Erhoehen Sie CPU und Speicher einer einzelnen Instanz. Nuetzlich fuer die Verarbeitung von mehr gleichzeitigen Anfragen pro Instanz.

**Horizontale Skalierung** — Fuehren Sie mehrere Instanzen hinter einem Load Balancer aus. Keine Sticky Sessions oder gemeinsamen Caches erforderlich. Jede Instanz ist vollstaendig unabhaengig.

**Skalierung auf Null** — Authagonal unterstuetzt Scale-to-Zero-Deployments (z.B. Azure Container Apps mit `minReplicas: 0`). Die erste Anfrage nach Leerlauf hat einen Kaltstart von einigen Sekunden, waehrend die .NET-Laufzeitumgebung initialisiert und Signaturschluessel aus dem Speicher geladen werden.
