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

Registrierungsendpunkte werden durch einen integrierten verteilten Rate Limiter geschuetzt (5 Registrierungen pro IP pro Stunde). Beim Betrieb mehrerer Instanzen werden die Zaehler der Ratenbegrenzung automatisch ueber ein Gossip-Protokoll zwischen allen Instanzen geteilt — keine externe Koordination erforderlich.

### Funktionsweise

Jede Instanz pflegt ihre eigenen Zaehler im Speicher mithilfe eines CRDT G-Counter. Instanzen entdecken sich gegenseitig ueber UDP Multicast und tauschen ihren Zustand alle paar Sekunden ueber HTTP aus. Der konsolidierte Zaehlerstand aller Instanzen wird fuer Ratenbegrenzungsentscheidungen verwendet.

Das bedeutet, dass Ratenbegrenzungen global durchgesetzt werden: Wenn ein Client 3 verschiedene Instanzen anspricht, wissen alle 3, dass die Gesamtzahl 3 betraegt, nicht jeweils 1.

### Cluster-Konfiguration

Clustering ist **standardmaessig aktiviert** ohne jegliche Konfiguration. Instanzen im selben Netzwerk entdecken sich automatisch ueber UDP Multicast (`239.42.42.42:19847`).

Fuer Umgebungen, in denen Multicast nicht verfuegbar ist (einige Cloud-VPCs), konfigurieren Sie eine lastverteilte interne URL als Fallback:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Um Clustering vollstaendig zu deaktivieren (nur lokale Ratenbegrenzung):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Siehe die Seite [Konfiguration](configuration) fuer alle Cluster-Einstellungen.

### Graceful Degradation

- **Keine Peers gefunden** — funktioniert als lokaler Rate Limiter (jede Instanz setzt ihr eigenes Limit durch)
- **Peer nicht erreichbar** — der letzte bekannte Zustand dieses Peers wird weiterhin verwendet; veraltete Peers werden nach 30 Sekunden entfernt
- **Multicast nicht verfuegbar** — Discovery schlaegt stillschweigend fehl; Gossip faellt auf `InternalUrl` zurueck, falls konfiguriert

## Skalierungsempfehlungen

**Vertikale Skalierung** — Erhoehen Sie CPU und Speicher einer einzelnen Instanz. Nuetzlich fuer die Verarbeitung von mehr gleichzeitigen Anfragen pro Instanz.

**Horizontale Skalierung** — Fuehren Sie mehrere Instanzen hinter einem Load Balancer aus. Keine Sticky Sessions oder gemeinsamen Caches erforderlich. Jede Instanz ist vollstaendig unabhaengig.

**Skalierung auf Null** — Authagonal unterstuetzt Scale-to-Zero-Deployments (z.B. Azure Container Apps mit `minReplicas: 0`). Die erste Anfrage nach Leerlauf hat einen Kaltstart von einigen Sekunden, waehrend die .NET-Laufzeitumgebung initialisiert und Signaturschluessel aus dem Speicher geladen werden.
