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

Consultez [Extensibilite](extensibility) pour tous les points de substitution et [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) pour un exemple complet.

## SPA de connexion (npm)

L'interface de connexion est publiee en tant que package npm pour la personnalisation :

```bash
npm install @authagonal/login
```

Le package fournit du JS et du CSS compiles -- importez les composants et les styles directement dans votre propre application React. Consultez [Serveur personnalise](custom-server) pour un guide complet.

## Liste de controle de securite pour la production

Avant d'exposer Authagonal a du trafic reel, verifiez les points suivants. Chaque point est detaille sur la page [Configuration](configuration).

- **Executez derriere un proxy terminant le TLS.** Authagonal doit etre place derriere un reverse proxy / ingress qui termine le TLS. Le cookie de session utilise `SecurePolicy = SameAsRequest` et HSTS n'est emis que sur HTTPS, le proxy doit donc transferer `X-Forwarded-Proto: https`. Definissez `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) sur le CIDR de votre ingress / de vos pods afin que l'IP du client et le schema ne puissent pas etre usurpes ; `ForwardedHeaders:ForwardLimit` vaut `1` par defaut (ne faire confiance qu'au dernier saut).
- **Definissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par defaut est **en texte brut** — sans Key Vault, les secrets des clients OIDC en amont et les graines TOTP / MFA sont stockes en clair dans Table Storage (et dans les sauvegardes). Configurez Key Vault pour tout deploiement en production.
- **Verrouillez l'API d'administration.** `AdminApi:Enabled` vaut **true** par defaut. Le scope d'administration (`AdminApi:Scope`, par defaut `authagonal-admin`) accorde la gestion complete et l'usurpation d'identite des utilisateurs. Restreignez l'acces reseau aux routes d'administration `/api/v1/*` et controlez strictement a qui le scope d'administration est emis, ou definissez `AdminApi:Enabled = false` s'il n'est pas utilise.
- **Protegez les points d'acces internes.** Definissez `Cluster:Secret` pour que `/_internal/cluster/gossip` et `/_internal/backchannel-logout` exigent l'en-tete `X-Cluster-Secret` — en particulier lorsque le gossip est achemine via un equilibreur de charge avec `Cluster:InternalUrl`.
- **Chiffrez les sauvegardes.** Avec le fournisseur de secrets en texte brut, les sauvegardes contiennent des secrets. La table `SigningKeys` est exclue des sauvegardes par defaut ; si vous l'activez via `Backup:IncludeSigningKeys`, la cible de sauvegarde doit etre chiffree au repos. Voir [Sauvegarde et restauration](backup-restore).

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
