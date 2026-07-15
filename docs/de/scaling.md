---
layout: default
title: Skalierung
locale: de
---

# Skalierung

Authagonal ist so konzipiert, dass es ohne spezielle Konfiguration sowohl vertikal als auch horizontal skaliert werden kann.

## Zustandslos durch Design

Alle persistenten Zustaende werden in Azure Table Storage gespeichert. Es gibt keinen In-Process-Zustand, der Sticky Sessions oder Koordination zwischen Instanzen erfordert:

- **Signaturschlüssel** — aus Table Storage geladen, stuendlich aktualisiert
- **Autorisierungscodes und Refresh-Tokens** — in Table Storage mit Einmalverwendung gespeichert
- **SAML Replay-Schutz** — Anfrage-IDs werden in Table Storage mit atomarem Löschen verfolgt
- **OIDC State und PKCE Verifier** — in Table Storage gespeichert
- **Client- und Provider-Konfiguration** — pro Anfrage aus Table Storage abgerufen

## Cookie-Verschlüsselung (Data Protection)

Die Data Protection Schlüssel von ASP.NET Core werden automatisch in Azure Blob Storage persistiert, wenn eine echte Azure Storage Verbindungszeichenfolge verwendet wird. Das bedeutet, dass Cookies, die von einer Instanz signiert wurden, von jeder anderen Instanz entschlüsselt werden können — keine Sticky Sessions erforderlich.

Für die lokale Entwicklung mit Azurite fallen Data Protection Schlüssel auf den standardmäßigen dateibasierten Speicher zurück.

Sie können auch eine explizite Blob-URI über die Konfiguration angeben:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Caches pro Instanz

Eine kleine Anzahl von häufig gelesenen, sich langsam ändernden Werten wird pro Instanz im Speicher zwischengespeichert, um Table Storage Roundtrips zu reduzieren:

| Daten | Cache-Dauer | Auswirkung bei Veralterung |
|---|---|---|
| OIDC Discovery-Dokumente | 60 Minuten (konfigurierbar) | Verzoegerte Erkennung von IdP-Schlüsselrotation |
| SAML IdP-Metadaten | 60 Minuten (konfigurierbar) | Gleich |
| CORS erlaubte Origins | 60 Minuten (konfigurierbar) | Neue Origins benötigen bis zu einer Stunde zur Verbreitung |

Diese Caches sind für den Produktionseinsatz akzeptabel. Alle Dauern sind über den Konfigurationsabschnitt `Cache` konfigurierbar — siehe [Konfiguration](configuration). Wenn Sie eine sofortige Verbreitung benötigen, starten Sie die betroffenen Instanzen neu.

## Ratenbegrenzung

Registrierungsendpunkte werden durch einen integrierten verteilten Rate Limiter geschuetzt (5 Registrierungen pro IP pro Stunde). Beim Betrieb mehrerer Instanzen werden die Zähler der Ratenbegrenzung automatisch über ein Gossip-Protokoll zwischen allen Instanzen geteilt — keine externe Koordination erforderlich.

### Funktionsweise

Jede Instanz pflegt ihre eigenen Zähler im Speicher mithilfe eines CRDT G-Counter. Instanzen entdecken sich gegenseitig über UDP Multicast und tauschen ihren Zustand alle paar Sekunden über HTTP aus. Der konsolidierte Zählerstand aller Instanzen wird für Ratenbegrenzungsentscheidungen verwendet.

Das bedeutet, dass Ratenbegrenzungen global durchgesetzt werden: Wenn ein Client 3 verschiedene Instanzen anspricht, wissen alle 3, dass die Gesamtzahl 3 betraegt, nicht jeweils 1.

### Knotenidentitaet

Jede Instanz generiert beim Start eine zufaellige hexadezimale Knoten-ID (z.B. `a3f1b2`). Diese ID identifiziert die Instanz in Gossip-Nachrichten und dem Ratenbegrenzungsstatus. Sie wird nicht persistiert -- bei jedem Neustart wird eine neue ID generiert.

Ein `ClusterLeaderService` laeuft auf jeder Instanz und wählt einen einzelnen Leader unter den entdeckten Peers (niedrigste Knoten-ID gewinnt). Die Fuehrung wird automatisch übertragen, wenn der Leader ausfaellt. Der Leader wird für cluster-weite Koordination verwendet — derzeit laeuft die Signaturschlüsselrotation (wenn aktiviert) nur auf dem Leader, um gleichzeitige Schlüsselerzeugung zu vermeiden.

### Cluster-Konfiguration

Clustering ist **standardmäßig aktiviert** ohne jegliche Konfiguration. Instanzen im selben Netzwerk entdecken sich automatisch über UDP Multicast (`239.42.42.42:19847`).

Für Umgebungen, in denen Multicast nicht verfügbar ist (einige Cloud-VPCs), konfigurieren Sie eine lastverteilte interne URL als Fallback:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Um Clustering vollständig zu deaktivieren (nur lokale Ratenbegrenzung):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Siehe die Seite [Konfiguration](configuration) für alle Cluster-Einstellungen.

### Graceful Degradation

- **Keine Peers gefunden** — funktioniert als lokaler Rate Limiter (jede Instanz setzt ihr eigenes Limit durch)
- **Peer nicht erreichbar** — der letzte bekannte Zustand dieses Peers wird weiterhin verwendet; veraltete Peers werden nach 30 Sekunden entfernt
- **Multicast nicht verfügbar** — Discovery schlaegt stillschweigend fehl; Gossip fällt auf `InternalUrl` zurück, falls konfiguriert

### Multi-Mandanten-Deployments

Im Multi-Mandanten-Modus (`AddAuthagonalCore()`) werden Hintergrunddienste wie `GrantReconciliationService` und `SigningKeyRotationService` nicht registriert -- der Host verwaltet diese pro Mandant. Nur `TokenCleanupService` laeuft bedingungslos.

## Heisse Partition des Namensindex

Die Admin-Namenspraefixsuche wird durch die Indextabellen `UserFirstNames` / `UserLastNames` gestuetzt, die eine **einzige heisse Partition** verwenden. Bei Skalierung begrenzt dies den Index-Schreibdurchsatz auf etwa 2.000 Operationen/Sek., was bei hoher Last zu einem Engpass beim Erstellen/Aktualisieren von Benutzern werden kann. Wenn Sie keine Admin-Namenssuche anbieten, setzen Sie `Storage:NameIndexesEnabled = false`, um diese Schreibvorgaenge vollständig zu vermeiden. Siehe [Konfiguration](configuration).

## Vertrauenswuerdiger Proxy und interne Endpunkte

Beim Betrieb mehrerer Instanzen hinter einem Load Balancer:

- **Weitergeleitete Header** — Ratenbegrenzung und Kontosperre basieren auf der Client-IP, die aus `X-Forwarded-For` aufgeloest wird. Setzen Sie `ForwardedHeaders:KnownNetworks` auf Ihr Ingress- / Pod-CIDR, damit die Client-IP nicht über Instanzen hinweg gefaelscht werden kann. `ForwardedHeaders:ForwardLimit` ist standardmäßig `1`. Siehe [Konfiguration](configuration#forwarded-headers-trusted-proxy).
- **Interne Endpunkte** — `/_internal/cluster/gossip` und `/_internal/backchannel-logout` werden anhand der Quell-IP geschuetzt (nur Loopback / privat), sofern nicht `Cluster:Secret` gesetzt ist. Wenn Gossip über einen Load Balancer geleitet wird (`Cluster:InternalUrl`), schreibt der LB die Quell-IP um, daher setzen Sie `Cluster:Secret`, und der Gossip-Aufrufer präsentiert es im Header `X-Cluster-Secret`.

## Skalierungsempfehlungen

**Vertikale Skalierung** — Erhoehen Sie CPU und Speicher einer einzelnen Instanz. Nützlich für die Verarbeitung von mehr gleichzeitigen Anfragen pro Instanz.

**Horizontale Skalierung** — Fuehren Sie mehrere Instanzen hinter einem Load Balancer aus. Keine Sticky Sessions oder gemeinsamen Caches erforderlich. Jede Instanz ist vollständig unabhängig.

**Skalierung auf Null** — Authagonal unterstützt Scale-to-Zero-Deployments (z.B. Azure Container Apps mit `minReplicas: 0`). Die erste Anfrage nach Leerlauf hat einen Kaltstart von einigen Sekunden, während die .NET-Laufzeitumgebung initialisiert und Signaturschlüssel aus dem Speicher geladen werden.
