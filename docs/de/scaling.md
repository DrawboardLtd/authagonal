---
layout: default
title: Skalierung
locale: de
---

# Skalierung

Authagonal ist so konzipiert, dass es ohne besondere Konfiguration sowohl vertikal als auch horizontal skaliert.

## Zustandslos durch Design

Alle persistenten Zustände werden im zugrunde liegenden Tabellenspeicher abgelegt: Azure Table Storage oder, im AWS-Backend, DynamoDB. Es gibt keinen In-Process-Zustand, der Sticky Sessions oder eine Koordination zwischen Instanzen erfordert:

- **Signaturschlüssel**: aus Table Storage geladen, stündlich aktualisiert
- **Autorisierungscodes und Refresh Tokens**: in Table Storage gespeichert, mit Erzwingung der Einmalverwendung
- **SAML-Replay-Schutz**: Anfrage-IDs werden in Table Storage verfolgt, mit atomarem Löschen
- **OIDC State und PKCE-Verifier**: in Table Storage gespeichert
- **Client- und Provider-Konfiguration**: pro Anfrage aus Table Storage abgerufen

## Cookie-Verschlüsselung (Data Protection)

Die Data-Protection-Schlüssel von ASP.NET Core werden automatisch in Azure Blob Storage persistiert, wenn eine echte Azure Storage-Verbindungszeichenfolge verwendet wird. Das bedeutet, dass Cookies, die von einer Instanz signiert wurden, von jeder anderen Instanz entschlüsselt werden können: Sticky Sessions sind nicht erforderlich.

Für die lokale Entwicklung mit Azurite fallen die Data-Protection-Schlüssel auf den standardmäßigen dateibasierten Speicher zurück.

Sie können über die Konfiguration auch auf eine explizite Blob-URI verweisen (der Managed-Identity-Pfad, in der Produktion bevorzugt):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Übergeben Sie im AWS-Backend einen S3-Client und einen Bucket an `AddAuthagonalAwsStorage`, um den Key Ring in S3 zu persistieren: Ohne diesen Schritt liegt der Key Ring nur im Arbeitsspeicher, und Cookies werden bei einem Neustart sowie über Knoten hinweg ungültig. Siehe [Installation → AWS-Backend](installation#aws-backend).

## Caches pro Instanz

Eine kleine Anzahl von häufig gelesenen, sich selten ändernden Werten wird pro Instanz im Speicher zwischengespeichert, um Roundtrips zu Table Storage zu reduzieren:

| Daten | Cache-Dauer | Auswirkung bei Veralterung |
|---|---|---|
| OIDC-Discovery-Dokumente | 60 Minuten (konfigurierbar) | Verzögerte Erkennung einer IdP-Schlüsselrotation |
| SAML-IdP-Metadaten | 60 Minuten (konfigurierbar) | Gleich |
| Zulässige CORS-Origins | 60 Minuten (konfigurierbar) | Neue Origins benötigen bis zu einer Stunde zur Verbreitung |

Diese Caches sind für den Produktionseinsatz unbedenklich. Alle Dauern lassen sich über den Konfigurationsabschnitt `Cache` konfigurieren: siehe [Konfiguration](configuration). Wenn Sie eine sofortige Verbreitung benötigen, starten Sie die betroffenen Instanzen neu.

## Ratenbegrenzung

Missbrauchsanfällige Endpunkte (Registrierung pro IP, Passwort-Zurücksetzen pro Ziel-E-Mail-Adresse, SCIM pro Client, dynamische Client-Registrierung pro IP; siehe [Konfiguration → Ratenbegrenzung](configuration#rate-limiting)) werden durch einen integrierten Rate Limiter geschützt.

Limits werden hinter der `IRateLimiter`-Abstraktion **In-Process pro Knoten** durchgesetzt, sodass die effektive Obergrenze bei N Instanzen dem N-Fachen des konfigurierten Werts entspricht. Das ist beabsichtigt: Der Limiter ist ein Backstop gegen ausufernden Missbrauch eines einzelnen Knotens, und das maßgebliche globale Limit gehört an den Rand (WAF / Ingress / CDN), der den gesamten Traffic sieht, bevor er lastverteilt wird.

## Clustering

Mehrere Instanzen koordinieren sich über eine **Leader-Wahl** und einen **knotenübergreifenden Event Bus**, beide hinter austauschbaren Backends:

- **Leader-Wahl**: eine lease-basierte Wahl (`Cluster:LeaseTtlSeconds`, Standard 30s, erneuert in etwa der Hälfte dieses Intervalls). Genau ein Knoten hält die Lease; die Führung wird automatisch übertragen, wenn der Leader ausfällt. An den Leader gebundene Arbeit (derzeit die Signaturschlüsselrotation, wenn aktiviert) läuft nur auf dem Leader, um eine gleichzeitige Schlüsselerzeugung zu vermeiden.
- **Event Bus**: knotenübergreifende Benachrichtigungen (z. B. Cache-Invalidierung in Multi-Mandanten-Hosts), abgefragt alle `Cluster:PollIntervalSeconds` (Standard 3s).

Jede Instanz generiert beim Start eine zufällige, 12-stellige hexadezimale Knoten-ID zu ihrer Identifizierung; sie wird nicht persistiert.

### Backends

Der **Standard ist In-Process**: Ein einzelner Knoten ist immer sein eigener Leader, und Ereignisse bleiben lokal, korrekt für eine einzelne Instanz ohne jede Konfiguration. Multi-Knoten-Deployments setzen über den `configureClustering`-Callback von `AddAuthagonal` ein echtes Backend ein:

```csharp
// Azure: Führung über eine Blob-Lease, Event Bus über ein Table-Log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: Führung + Event Bus über DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` registrieren nur den Event Bus und behalten die In-Process-Lease (immer Leader) bei: Verwenden Sie sie auf Knoten, die Cluster-Ereignisse empfangen müssen, aber niemals um die Führung konkurrieren dürfen.

> **Hinweis:** Beim In-Process-Standard auf mehreren Knoten glaubt *jeder* Knoten, der Leader zu sein. Das ist für die meisten Workloads unbedenklich, aber aktivieren Sie ein echtes Lease-Backend, bevor Sie `Auth:KeyRotationEnabled` über mehrere Instanzen hinweg einschalten.

Siehe die Seite [Konfiguration](configuration#cluster) für alle Cluster-Einstellungen.

### Multi-Mandanten-Deployments

Im Multi-Mandanten-Modus (`AddAuthagonalCore()`) werden keine Hintergrunddienste registriert: `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` und die Config-Seed-Dienste sind allesamt Teil der Single-Mandanten-Komposition `AddAuthagonal()`. Der Host verwaltet diese pro Mandant.

## Heiße Partition des Namensindex

Die Admin-Namenspräfixsuche wird durch die Indextabellen `UserFirstNames` / `UserLastNames` gestützt, die eine **einzige heiße Partition** verwenden. Bei Skalierung begrenzt dies den Index-Schreibdurchsatz auf etwa 2.000 Operationen/Sek., was bei hoher Last zu einem Engpass beim Erstellen/Aktualisieren von Benutzern werden kann. Wenn Sie keine Admin-Namenssuche anbieten, setzen Sie `Storage:NameIndexesEnabled = false`, um diese Schreibvorgänge vollständig zu vermeiden. Siehe [Konfiguration](configuration).

## Vertrauenswürdiger Proxy und interne Endpunkte

Beim Betrieb mehrerer Instanzen hinter einem Load Balancer:

- **Weitergeleitete Header**: Ratenbegrenzung und Kontosperre basieren auf der Client-IP, die aus `X-Forwarded-For` aufgelöst wird. Setzen Sie `ForwardedHeaders:KnownNetworks` auf Ihr Ingress- / Pod-CIDR, damit die Client-IP nicht instanzübergreifend gefälscht werden kann. `ForwardedHeaders:ForwardLimit` ist standardmäßig `1`. Siehe [Konfiguration](configuration#forwarded-headers-trusted-proxy).
- **Interne Endpunkte**: `/_internal/backchannel-logout` erfordert `Cluster:Secret` im Header `X-Cluster-Secret` (Vergleich in konstanter Zeit). Ohne das Geheimnis autorisiert der Endpunkt niemanden und antwortet mit 404 — die Quell-IP wird nicht als Credential behandelt, denn Loopback ist das, was ein Reverse-Proxy auf demselben Host für jede weitergeleitete Anfrage präsentiert, und ein privater Bereich ist in einem gemeinsam genutzten Cluster-Netzwerk jede benachbarte Workload. `Cluster:AllowLoopbackWithoutSecret` ist ein reines Entwicklungs-Opt-in, das einen Loopback-Peer vor der Weiterleitung wieder zulässt. Das ausgelieferte Produkt ruft diese Route nie auf (die Session-Verteilung läuft in-process über `SessionTermination`), sie ist also nur für eine selbst gebaute Verteilung relevant.

## Skalierungsempfehlungen

**Vertikale Skalierung**: Erhöhen Sie CPU und Arbeitsspeicher einer einzelnen Instanz. Nützlich, um mehr gleichzeitige Anfragen pro Instanz zu verarbeiten.

**Horizontale Skalierung**: Führen Sie mehrere Instanzen hinter einem Load Balancer aus. Keine Sticky Sessions oder gemeinsamen Caches erforderlich. Jede Instanz ist vollständig unabhängig.

**Skalierung auf null**: Authagonal unterstützt Scale-to-Zero-Deployments (z. B. Azure Container Apps mit `minReplicas: 0`). Die erste Anfrage nach einer Leerlaufphase hat einen Kaltstart von einigen Sekunden, während die .NET-Laufzeit initialisiert und Signaturschlüssel aus dem Speicher geladen werden.
