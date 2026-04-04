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
  authagonal
```

## Docker Compose

Fuer die lokale Entwicklung mit Azurite (Azure Storage Emulator):

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
- Node.js 22+

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
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

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

Alle Erweiterungspunkte finden Sie unter [Erweiterbarkeit](extensibility). Ein vollstaendiges Beispiel finden Sie unter [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server).

## Login-SPA (npm)

Die Login-Oberflaeche wird als npm-Paket zur Anpassung veroeffentlicht:

```bash
npm install @drawboard/authagonal-login
```

Passen Sie es ueber `branding.json` an (siehe [Branding](branding)) und erstellen Sie die SPA in das `wwwroot/`-Verzeichnis Ihres Servers:

```bash
cd node_modules/@drawboard/authagonal-login
npx vite build
cp -r dist/* /path/to/wwwroot/
```

## Migrationstool

Fuer die Migration von Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Details finden Sie unter [Migration](migration).
