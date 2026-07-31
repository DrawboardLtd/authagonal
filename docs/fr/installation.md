---
layout: default
title: Installation
locale: fr
---

# Installation

## Docker (recommandé)

Téléchargez et exécutez l'image préconstruite :

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose

Pour le développement local avec Azurite (émulateur Azure Storage) :

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

### Prérequis

- .NET 10 SDK
- Node.js 24+

### Compilation

```bash
# Tout compiler
dotnet build

# Compiler la SPA de connexion
cd login-app
npm ci
npm run build

# Exécuter le serveur
dotnet run --project src/Authagonal.Server
```

### Compilation Docker

```bash
# Image du serveur (multi-étapes : compile la SPA + .NET dans une seule image)
docker build -t authagonal .

# Outil de migration
docker build -f Dockerfile.migration -t authagonal-migration .
```

## En tant que bibliothèque (NuGet)

Référencez les packages Authagonal dans votre propre projet ASP.NET Core :

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Le package du fournisseur de stockage est interchangeable : `Authagonal.AzureProvider` pour Azure Table Storage (le câblage par défaut de `AddAuthagonal()`), `Authagonal.SqlProvider` pour PostgreSQL ou SQLite auto-hébergés (voir [backend SQL](#backend-sql)), ou `Authagonal.AwsProvider` pour DynamoDB / S3 / Secrets Manager (voir [backend AWS](#backend-aws)).

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

Consultez [Extensibilité](extensibility) pour tous les points de substitution et [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) pour un exemple complet.

### Email

L'expéditeur [Resend](https://resend.com) intégré s'active automatiquement lorsque `Email:ResendApiKey` et `Email:SenderEmail` sont configurés, sans enregistrement de service. Sans aucun `IEmailService`, les emails de vérification et de réinitialisation de mot de passe sont **ignorés silencieusement**, et comme la connexion exige un email confirmé par défaut, les utilisateurs auto-inscrits ne peuvent jamais se connecter (`UseAuthagonal` journalise un avertissement au démarrage). Définissez les clés `Email:*`, enregistrez votre propre `IEmailService` avant `AddAuthagonal()`, ou listez vos domaines dans `Auth:AutoConfirmEmailDomains` pour ignorer la vérification (dev/test uniquement). Voir [Configuration → Email](configuration#email).

## Backend SQL

Pour exécuter sur votre propre base de données plutôt que sur un service cloud, référencez `Authagonal.SqlProvider` et enregistrez-le **avant** `AddAuthagonal()` : ce sont ces enregistrements qui font que `AddAuthagonal()` ignore son câblage Azure Table Storage :

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Les tables reproduisent un pour un les dispositions Azure et DynamoDB et sont créées au démarrage si elles sont absentes (chaque instruction est un `IF NOT EXISTS`, il est donc sans danger que plusieurs pods se disputent la création, et l'opération ne fait rien face à un schéma que vous avez provisionné vous-même). Aucune configuration `Storage:*` n'est nécessaire. Le trousseau de clés Data Protection est persisté dans la même base, de sorte que les cookies et les jetons antiforgery survivent aux redémarrages et fonctionnent entre pods sans service supplémentaire.

SQLite sérialise les écritures : c'est donc un backend mononœud, et le bail en processus ainsi que le bus d'événements de cluster enregistrés par défaut y sont la bonne combinaison. Un déploiement PostgreSQL multi-pods voudra `clustering.UseSql(dataSource)` pour l'élection du leader.

> **Collation.** Sur PostgreSQL, les colonnes de clé sont fixées à `COLLATE "C"`. Le schéma de clés est ordinal sur les octets de bout en bout (bornes de préfixe, plages de partition par environnement, le balayage d'expiration des octrois, la pagination par keyset), et une base créée avec une collation linguistique -- `en_US.UTF-8` et les locales ICU sont les valeurs par défaut courantes -- ordonnerait la ponctuation et la casse différemment et renverrait silencieusement les mauvaises lignes. Cette fixation rend la disposition indépendante de la façon dont la base a été créée ; vous n'avez pas besoin de la créer d'une manière particulière.

Consultez le [README du package](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) pour la disposition des tables, les primitives de concurrence derrière chaque garantie d'usage unique, et la façon d'ajouter un dialecte pour un autre moteur.

## Backend AWS

Pour exécuter sur AWS plutôt que sur Azure, référencez `Authagonal.AwsProvider` et enregistrez le bundle AWS **avant** `AddAuthagonal()` : ce sont ces enregistrements qui font que `AddAuthagonal()` ignore son câblage Azure Table Storage :

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Les tables DynamoDB reflètent la disposition Azure une pour une et sont garanties au démarrage (idempotent : sans effet lorsqu'elles sont déjà provisionnées par Terraform). Les identifiants sont résolus via la chaîne AWS standard (env / rôle d'instance EC2 / IRSA), il n'y a donc pas de distinction chaîne-de-connexion contre identité-gérée : aucune configuration `Storage:*` n'est nécessaire.

> ⚠️ **Clés S3 DataProtection.** Sans client S3 + bucket, le trousseau de clés ASP.NET Core Data Protection est conservé en mémoire, ce qui convient pour un nœud unique en dev, mais les cookies et les jetons antiforgery cessent de fonctionner après un redémarrage et entre les nœuds en production. Passez toujours le client S3 et le bucket pour un déploiement AWS de production.

## SPA de connexion (npm)

L'interface de connexion est publiée en tant que package npm pour la personnalisation :

```bash
npm install @authagonal/login
```

Le package fournit du JS et du CSS compilés : importez les composants et les styles directement dans votre propre application React. Consultez [Serveur personnalisé](custom-server) pour un guide complet.

## Liste de contrôle de sécurité pour la production

Avant d'exposer Authagonal à du trafic réel, vérifiez les points suivants. Chaque point est détaillé sur la page [Configuration](configuration).

- **Exécutez derrière un proxy terminant le TLS.** Authagonal doit être placé derrière un reverse proxy / ingress qui termine le TLS. Le cookie de session utilise `SecurePolicy = SameAsRequest` et HSTS n'est émis que sur HTTPS, le proxy doit donc transférer `X-Forwarded-Proto: https`. Définissez `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) sur le CIDR de votre ingress / de vos pods afin que l'IP du client et le schéma ne puissent pas être usurpés ; `ForwardedHeaders:ForwardLimit` vaut `1` par défaut (ne faire confiance qu'au dernier saut).
- **Définissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par défaut est **en texte brut** : sans Key Vault, les secrets des clients OIDC en amont et les graines TOTP / MFA sont stockés en clair dans Table Storage (et dans les sauvegardes). Configurez Key Vault pour tout déploiement en production.
- **Verrouillez l'API d'administration.** `AdminApi:Enabled` vaut **true** par défaut. Le scope d'administration (`AdminApi:Scope`, par défaut `authagonal-admin`) accorde la gestion complète et l'usurpation d'identité des utilisateurs. Restreignez l'accès réseau aux routes d'administration `/api/v1/*` et contrôlez strictement à qui le scope d'administration est émis, ou définissez `AdminApi:Enabled = false` s'il n'est pas utilisé.
- **Protégez les points d'accès internes.** Définissez `Cluster:Secret` pour que le point d'accès interne `/_internal/backchannel-logout` exige l'en-tête `X-Cluster-Secret` (comparé en temps constant). Lorsqu'il n'est pas défini, il n'accepte que les IP source de boucle locale / privées (RFC 1918 / lien-local / ULA) : assurez-vous que la confiance des en-têtes transférés est configurée pour qu'un appelant externe ne puisse pas paraître interne.
- **Chiffrez les sauvegardes.** Avec le fournisseur de secrets en texte brut, les sauvegardes contiennent des secrets. La table `SigningKeys` est exclue des sauvegardes par défaut ; si vous l'activez via `Backup:IncludeSigningKeys`, la cible de sauvegarde doit être chiffrée au repos. Voir [Sauvegarde et restauration](backup-restore).

## Outil de migration

Pour migrer depuis Duende IdentityServer + SQL Server :

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consultez [Migration](migration) pour plus de détails.
