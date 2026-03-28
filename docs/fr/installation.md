---
layout: default
title: Installation
locale: fr
---

# Installation

## Docker (recommande)

Telechargez et executez l'image preconstruite :

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  authagonal
```

## Docker Compose

Pour le developpement local avec Azurite (emulateur Azure Storage) :

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

## Compilation depuis les sources

### Prerequis

- .NET 10 SDK
- Node.js 22+

### Compilation

```bash
# Tout compiler
dotnet build

# Compiler la SPA de connexion
cd login-app
npm ci
npm run build

# Executer le serveur
dotnet run --project src/Authagonal.Server
```

### Compilation Docker

```bash
# Image du serveur (multi-etapes : compile la SPA + .NET dans une seule image)
docker build -t authagonal .

# Outil de migration
docker build -f Dockerfile.migration -t authagonal-migration .
```

## En tant que bibliotheque (NuGet)

Referencez les packages Authagonal dans votre propre projet ASP.NET Core :

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

Puis composez-le dans votre `Program.cs` :

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

Consultez [Extensibilite](extensibility) pour tous les points de substitution et [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) pour un exemple complet.

## SPA de connexion (npm)

L'interface de connexion est publiee en tant que package npm pour la personnalisation :

```bash
npm install @drawboard/authagonal-login
```

Personnalisez via `branding.json` (voir [Personnalisation visuelle](branding)) et compilez la SPA dans le repertoire `wwwroot/` de votre serveur :

```bash
cd node_modules/@drawboard/authagonal-login
npx vite build
cp -r dist/* /path/to/wwwroot/
```

## Outil de migration

Pour migrer depuis Duende IdentityServer + SQL Server :

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consultez [Migration](migration) pour plus de details.
