---
layout: default
title: Installation
---

# Installation

## Docker (recommended)

Pull and run the pre-built image:

```bash
# PostgreSQL
docker run -p 8080:8080 \
  -e Storage__Provider=postgres \
  -e Storage__ConnectionString="Host=db;Database=authagonal;Username=auth;Password=…" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal

# SQLite — mount a volume so the file survives the container
docker run -p 8080:8080 -v authagonal:/data \
  -e Storage__Provider=sqlite \
  -e Storage__ConnectionString="Data Source=/data/authagonal.db" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal

# Azure Table Storage (the default provider)
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose

The repo ships a compose file per backend. The default needs nothing but Docker:

```bash
docker compose up                                                        # SQLite
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up   # PostgreSQL
docker compose -f docker-compose.yml -f docker-compose.azure.yml up      # Azure Table Storage (Azurite)
```

The overrides replace the storage settings on the same `authagonal` service, so exactly one server runs
whichever you pick.

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
dotnet run --project src/Authagonal.Host
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

The storage provider package is pluggable: `Authagonal.SqlProvider` for self-hosted PostgreSQL or SQLite (see [SQL backend](#sql-backend) below), `Authagonal.AzureProvider` for Azure Table Storage (the default `AddAuthagonal()` wiring), or `Authagonal.AwsProvider` for DynamoDB / S3 / Secrets Manager (see [AWS backend](#aws-backend)).

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

To run on your own database rather than a cloud service, set `Storage:Provider`. The published Docker image already carries the provider, so this needs no code change:

```bash
# PostgreSQL — the production self-hosted backend
Storage__Provider=postgres
Storage__ConnectionString=Host=db;Database=authagonal;Username=auth;Password=…
Storage__Schema=public                                       # optional, defaults to "public"
```

```bash
# SQLite — one file, no server
Storage__Provider=sqlite
Storage__ConnectionString=Data Source=/data/authagonal.db    # optional, defaults to ./authagonal.db
```

In a library host, reference `Authagonal.SqlProvider` and register it **before** `AddAuthagonal()` — those registrations are what make `AddAuthagonal()` skip its Azure Table Storage wiring, the same contract as the AWS provider:

```csharp
using Authagonal.SqlProvider;

builder.Services.AddAuthagonalPostgres("Host=db;Database=authagonal;Username=auth;Password=…");
// or: builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");
builder.Services.AddAuthagonal(builder.Configuration);
```

To keep the choice in configuration instead — what `Authagonal.Host` does for the shipped image — call the config-driven form, which is a no-op unless `Storage:Provider` names a SQL backend:

```csharp
builder.Services.AddAuthagonalSqlStorageFromConfiguration(builder.Configuration);
builder.Services.AddAuthagonal(builder.Configuration);
```

The PostgreSQL and SQLite drivers deliberately do **not** ship as dependencies of `Authagonal.Server`, so referencing the server library never pulls Npgsql or the SQLite native binaries into an Azure- or AWS-only application. Setting `Storage:Provider=postgres` without wiring the provider fails at startup with a message naming the call to add.

Tables mirror the Azure and DynamoDB layouts one-for-one and are created on startup if absent (every statement is `IF NOT EXISTS`, so it is safe to race across pods and a no-op against a schema you provisioned yourself). The DataProtection key ring is persisted to the same database automatically, so cookies and antiforgery tokens survive restarts and work across pods with no extra service.

Choosing between them: PostgreSQL for anything with more than one pod; SQLite for the quick start, embedded library hosts, CI, and small single-node deployments. SQLite serializes writers by construction, so it is a single-node backend — the in-process lease and cluster event bus registered by default are the correct pairing there, while a multi-pod PostgreSQL deployment wants `clustering.UseSql(dataSource)` for leader election.

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
