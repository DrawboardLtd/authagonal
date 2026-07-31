---
layout: default
title: Installation
locale: de
---

# Installation

## Docker (empfohlen)

Laden und starten Sie das vorgefertigte Image:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose

Für die lokale Entwicklung mit Azurite (Azure Storage Emulator):

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
    depends_on:
      - azurite
```

```bash
docker compose up
```

## Aus dem Quellcode erstellen

### Voraussetzungen

- .NET 10 SDK
- Node.js 24+

### Erstellen

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Docker-Build

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Als Bibliothek (NuGet)

Referenzieren Sie die Authagonal-Pakete in Ihrem eigenen ASP.NET Core-Projekt:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Das Speicheranbieter-Paket ist austauschbar: `Authagonal.AzureProvider` für Azure Table Storage (die Standardverkabelung von `AddAuthagonal()`), `Authagonal.SqlProvider` für selbst betriebenes PostgreSQL oder SQLite (siehe [SQL-Backend](#sql-backend)), oder `Authagonal.AwsProvider` für DynamoDB / S3 / Secrets Manager (siehe [AWS-Backend](#aws-backend)).

Integrieren Sie es dann in Ihre `Program.cs`:

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Alle Erweiterungspunkte finden Sie unter [Erweiterbarkeit](extensibility). Ein vollständiges Beispiel finden Sie unter [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server).

### E-Mail

Der integrierte [Resend](https://resend.com)-Versand aktiviert sich automatisch, wenn `Email:ResendApiKey` und `Email:SenderEmail` konfiguriert sind: eine gesonderte Dienstregistrierung ist dann nicht nötig. Ohne einen `IEmailService` werden Verifizierungs- und Passwort-Zurücksetzungs-E-Mails **stillschweigend verworfen**, und da die Anmeldung standardmäßig eine bestätigte E-Mail-Adresse voraussetzt, können sich selbst registrierte Benutzer niemals anmelden (`UseAuthagonal` protokolliert beim Start eine Warnung). Setzen Sie entweder die `Email:*`-Schlüssel, registrieren Sie Ihren eigenen `IEmailService` vor `AddAuthagonal()`, oder listen Sie Ihre Domänen in `Auth:AutoConfirmEmailDomains` auf, um die Verifizierung zu überspringen (nur für Entwicklung/Tests). Siehe [Konfiguration → E-Mail](configuration#email).

## SQL-Backend

Um Authagonal auf Ihrer eigenen Datenbank statt auf einem Cloud-Dienst zu betreiben, referenzieren Sie `Authagonal.SqlProvider` und registrieren Sie ihn **vor** `AddAuthagonal()`: diese Registrierungen sorgen dafür, dass `AddAuthagonal()` seine Azure-Table-Storage-Verkabelung überspringt:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Die Tabellen entsprechen eins zu eins dem Azure- und DynamoDB-Layout und werden beim Start angelegt, falls sie fehlen (jede Anweisung ist ein `IF NOT EXISTS`, sodass ein Wettlauf mehrerer Pods unbedenklich ist und gegen ein selbst bereitgestelltes Schema nichts geschieht). Eine `Storage:*`-Konfiguration ist nicht nötig. Der Schlüsselring für Data Protection wird in derselben Datenbank abgelegt, sodass Cookies und Antiforgery-Token Neustarts überstehen und über mehrere Pods hinweg funktionieren, ohne einen zusätzlichen Dienst.

SQLite serialisiert Schreibvorgänge und ist damit ein Backend für einen einzelnen Knoten -- die standardmäßig registrierte prozessinterne Lease und der prozessinterne Cluster-Ereignisbus sind dort die richtige Kombination. Ein PostgreSQL-Deployment über mehrere Pods möchte `clustering.UseSql(dataSource)` für die Leader-Wahl.

> **Sortierung (Collation).** Unter PostgreSQL werden die Schlüsselspalten auf `COLLATE "C"` festgelegt. Das Schlüsselschema ist durchgängig byte-ordinal (Präfixgrenzen, Umgebungspartitionsbereiche, das Aufräumen abgelaufener Gewährungen, Keyset-Paging), und eine Datenbank, die mit einer sprachlichen Sortierung angelegt wurde -- `en_US.UTF-8` und ICU-Locales sind die üblichen Standardwerte -- würde Satzzeichen und Groß-/Kleinschreibung anders ordnen und stillschweigend die falschen Zeilen zurückgeben. Die Festlegung macht das Layout unabhängig davon, wie die Datenbank erstellt wurde; Sie müssen sie in keiner bestimmten Weise anlegen.

Das [Paket-README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) beschreibt das Tabellen-Layout, die Nebenläufigkeitsprimitive hinter jeder Einmal-Garantie und wie ein Dialekt für eine andere Engine ergänzt wird.

## AWS-Backend

Um Authagonal statt auf Azure auf AWS auszuführen, referenzieren Sie `Authagonal.AwsProvider` und registrieren Sie das AWS-Bundle **vor** `AddAuthagonal()`: diese Registrierungen sorgen dafür, dass `AddAuthagonal()` seine Azure-Table-Storage-Verkabelung überspringt:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Die DynamoDB-Tabellen entsprechen eins zu eins dem Azure-Layout und werden beim Start sichergestellt (idempotent: ein No-Op, wenn sie bereits über Terraform bereitgestellt wurden). Anmeldeinformationen werden über die Standard-AWS-Kette aufgelöst (Umgebungsvariablen / EC2-Instance-Rolle / IRSA), sodass es keine Trennung zwischen Verbindungszeichenfolge und verwalteter Identität gibt: keine `Storage:*`-Konfiguration ist nötig.

> ⚠️ **S3-Schlüssel für Data Protection.** Ohne einen S3-Client und ein Bucket wird der Schlüsselring von ASP.NET Core Data Protection im Arbeitsspeicher gehalten: das funktioniert für einen einzelnen Knoten in der Entwicklung, aber Cookies und Antiforgery-Token brechen bei einem Neustart und über mehrere Knoten hinweg in der Produktion. Übergeben Sie für ein produktives AWS-Deployment immer den S3-Client und das Bucket.

## Login-SPA (npm)

Die Login-Oberfläche wird als npm-Paket zur Anpassung veröffentlicht:

```bash
npm install @authagonal/login
```

Das Paket liefert kompiliertes JS und CSS. Importieren Sie Komponenten und Stile direkt in Ihre eigene React-App. Siehe [Benutzerdefinierter Server](custom-server) für eine vollständige Anleitung.

## Sicherheits-Checkliste für die Produktion

Bevor Sie Authagonal echtem Datenverkehr aussetzen, bestätigen Sie Folgendes. Jeder Punkt wird auf der Seite [Konfiguration](configuration) ausführlich beschrieben.

- **Hinter einem TLS-terminierenden Proxy betreiben — und ihn deklarieren.** Authagonal muss hinter einem Reverse-Proxy / Ingress laufen, der TLS terminiert (oder TLS selbst terminieren). HSTS wird nur bei HTTPS gesendet und `/connect/*` lehnt Klartext ab, sodass der Proxy `X-Forwarded-Proto: https` weiterleiten muss — und dieser Header wird ignoriert, solange Sie `ForwardedHeaders:KnownNetworks` (oder `KnownProxies`) nicht auf das CIDR bzw. die Adresse Ihres Proxys setzen. Verwenden Sie `["0.0.0.0/0", "::/0"]`, wenn der Proxy keine feste Adresse hat und nichts anderes den Prozess erreichen kann. `ForwardedHeaders:ForwardLimit` ist standardmäßig `1` (nur dem letzten Hop vertrauen).
- **`SecretProvider:VaultUri` setzen.** Der Standard-Geheimnis-Anbieter speichert im **Klartext**: ohne Key Vault werden Geheimnisse von vorgelagerten OIDC-Clients sowie TOTP-/MFA-Seeds im Klartext in Table Storage (und in Sicherungen) gespeichert. Konfigurieren Sie Key Vault für jedes Produktions-Deployment.
- **Die Admin-API absichern.** `AdminApi:Enabled` ist standardmäßig **true**. Der Admin-Scope (`AdminApi:Scope`, Standard `authagonal-admin`) gewährt vollständige Verwaltung und Benutzer-Imitation. Beschränken Sie die `/api/v1/*`-Admin-Routen auf Netzwerkebene und kontrollieren Sie streng, wem der Admin-Scope ausgestellt wird, oder setzen Sie `AdminApi:Enabled = false`, falls nicht verwendet.
- **Interne Endpunkte schützen.** Setzen Sie `Cluster:Secret`, damit der interne Endpunkt `/_internal/backchannel-logout` den Header `X-Cluster-Secret` erfordert (der Vergleich erfolgt in konstanter Zeit). Ist er nicht gesetzt, akzeptiert er nur Anfragen von Loopback- oder privaten Quell-IPs (RFC 1918 / Link-Local / ULA): stellen Sie sicher, dass Ihre Konfiguration für Weitergeleitete Header korrekt eingerichtet ist, damit ein externer Aufrufer nicht als intern erscheinen kann.
- **Sicherungen verschlüsseln.** Mit dem Klartext-Geheimnis-Anbieter enthalten Sicherungen Geheimnisse. Die Tabelle `SigningKeys` ist standardmäßig von Sicherungen ausgeschlossen; wenn Sie sich über `Backup:IncludeSigningKeys` dafür entscheiden, muss das Sicherungsziel verschlüsselt im Ruhezustand sein. Siehe [Sicherung & Wiederherstellung](backup-restore).

## Migrationstool

Für die Migration von Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Details finden Sie unter [Migration](migration).
</content>
