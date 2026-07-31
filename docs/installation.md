---
layout: default
title: Installation
---

# Installation

## Docker (recommended)

Pull and run the pre-built image:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose

For local development with Azurite (Azure Storage emulator):

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
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` is a development setting.** RFC 6749 §3.1/§3.2 require TLS at the authorization and token endpoints, so Authagonal refuses non-https requests to `/connect/*` unless this is set. The scheme is read after forwarded-header processing, so a proxy that terminates TLS and forwards `X-Forwarded-Proto: https` satisfies the requirement with the setting left off — which is what every deployment reachable by anyone but you should do. With it on, an on-path observer reads the authorization code, the client secret in the `Authorization: Basic` header, and the access and refresh tokens. See [Configuration](configuration#authentication).

## Building from Source

### Prerequisites

- .NET 10 SDK
- Node.js 24+

### Build

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

### Docker Build

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## As a Library (NuGet)

Reference the Authagonal packages in your own ASP.NET Core project:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

The storage provider package is pluggable: `Authagonal.AzureProvider` for Azure Table Storage (the default `AddAuthagonal()` wiring), `Authagonal.SqlProvider` for self-hosted PostgreSQL or SQLite (see [SQL backend](#sql-backend)), or `Authagonal.AwsProvider` for DynamoDB / S3 / Secrets Manager (see [AWS backend](#aws-backend)).

Then compose it into your `Program.cs`:

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

See [Extensibility](extensibility) for all override points and [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) for a complete example.

### Email

The built-in [Resend](https://resend.com) sender activates automatically when `Email:ResendApiKey` and `Email:SenderEmail` are configured, no service registration needed. Without any `IEmailService`, verification and password-reset emails are **silently discarded**, and because login requires a confirmed email by default, self-registered users can never sign in (`UseAuthagonal` logs a warning at startup). Either set the `Email:*` keys, register your own `IEmailService` before `AddAuthagonal()`, or list your domains in `Auth:AutoConfirmEmailDomains` to skip verification (dev/test only). See [Configuration → Email](configuration#email).

## SQL backend

To run on your own database instead of a cloud service, reference `Authagonal.SqlProvider` and register it **before** `AddAuthagonal()`, those registrations are what make `AddAuthagonal()` skip its Azure Table Storage wiring:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Tables mirror the Azure and DynamoDB layouts one-for-one and are created on startup if absent (every statement is `IF NOT EXISTS`, so it is safe to race across pods and a no-op against a schema you provisioned yourself). No `Storage:*` configuration is needed. The DataProtection key ring is persisted to the same database, so cookies and antiforgery tokens survive restarts and work across pods with no extra service.

SQLite serializes writers, so it is a single-node backend — the in-process lease and cluster event bus registered by default are the correct pairing there. A multi-pod PostgreSQL deployment wants `clustering.UseSql(dataSource)` for leader election.

> **Collation.** On PostgreSQL the key columns are pinned to `COLLATE "C"`. The key scheme is byte-ordinal throughout (prefix bounds, env-partition ranges, the grant expiry sweep, keyset paging), and a database created with a linguistic collation — `en_US.UTF-8` and ICU locales are the common defaults — would order punctuation and case differently and silently return the wrong rows. The pin makes the layout independent of how the database was created; you do not need to create it any particular way.

See the [package README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) for the table layout, the concurrency primitives behind each single-use guarantee, and how to add a dialect for another engine.

## AWS backend

To run on AWS instead of Azure, reference `Authagonal.AwsProvider` and register the AWS bundle **before** `AddAuthagonal()`, those registrations are what make `AddAuthagonal()` skip its Azure Table Storage wiring:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

The DynamoDB tables mirror the Azure layout one-for-one and are ensured on startup (idempotent, a no-op when they're already provisioned by Terraform). Credentials resolve via the standard AWS chain (env / EC2 instance role / IRSA), so there is no connection-string-vs-managed-identity split, no `Storage:*` configuration is needed.

> ⚠️ **S3 DataProtection keys.** Without an S3 client + bucket, the ASP.NET Core Data Protection key ring is held in memory, fine for a single node in dev, but cookies and antiforgery tokens break on restart and across nodes in production. Always pass the S3 client and bucket for a production AWS deployment.

## Login SPA (npm)

The login UI is published as an npm package for customization:

```bash
npm install @authagonal/login
```

The package ships compiled JS and CSS, import components and styles directly in your own React app. See [Custom Server](custom-server) for a full walkthrough.

## Production security checklist

Before exposing Authagonal to real traffic, confirm the following. Each item is detailed on the [Configuration](configuration) page.

- **Run behind a TLS-terminating proxy.** Authagonal must sit behind a reverse proxy / ingress that terminates TLS. The session cookie uses `SecurePolicy = SameAsRequest` and HSTS is only emitted on HTTPS, so the proxy must forward `X-Forwarded-Proto: https`. Set `ForwardedHeaders:KnownNetworks` (or `KnownProxies`) to your ingress / pod CIDR so the client IP and scheme cannot be spoofed; `ForwardedHeaders:ForwardLimit` defaults to `1` (trust only the last hop).
- **Set `SecretProvider:VaultUri`.** The default secret provider is **plaintext**: without Key Vault, upstream OIDC client secrets and TOTP / MFA seeds are stored in cleartext in Table Storage (and in backups). Configure Key Vault for any production deployment.
- **Lock down the admin API.** `AdminApi:Enabled` defaults to **true**. The admin scope (`AdminApi:Scope`, default `authagonal-admin`) grants full management and user impersonation. Network-restrict the `/api/v1/*` admin routes and tightly control who is issued the admin scope, or set `AdminApi:Enabled = false` if unused.
- **Protect internal endpoints.** Set `Cluster:Secret` so the internal `/_internal/backchannel-logout` endpoint requires the `X-Cluster-Secret` header (compared in constant time). When unset, it accepts only loopback / private (RFC 1918 / link-local / ULA) source IPs, make sure your forwarded-headers trust is configured so an external caller can't appear internal.
- **Encrypt backups.** With the plaintext secret provider, backups contain secrets. The `SigningKeys` table is excluded from backups by default; if you opt in via `Backup:IncludeSigningKeys`, the backup target must be encrypted at rest. See [Backup & Restore](backup-restore).

## Migration Tool

For migrating from Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

See [Migration](migration) for details.
