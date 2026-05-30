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
  authagonal
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
    depends_on:
      - azurite
```

```bash
docker compose up
```

## Building from Source

### Prerequisites

- .NET 10 SDK
- Node.js 22+

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
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

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

## Login SPA (npm)

The login UI is published as an npm package for customization:

```bash
npm install @authagonal/login
```

The package ships compiled JS and CSS — import components and styles directly in your own React app. See [Custom Server](custom-server) for a full walkthrough.

## Production security checklist

Before exposing Authagonal to real traffic, confirm the following. Each item is detailed on the [Configuration](configuration) page.

- **Run behind a TLS-terminating proxy.** Authagonal must sit behind a reverse proxy / ingress that terminates TLS. The session cookie uses `SecurePolicy = SameAsRequest` and HSTS is only emitted on HTTPS, so the proxy must forward `X-Forwarded-Proto: https`. Set `ForwardedHeaders:KnownNetworks` (or `KnownProxies`) to your ingress / pod CIDR so the client IP and scheme cannot be spoofed; `ForwardedHeaders:ForwardLimit` defaults to `1` (trust only the last hop).
- **Set `SecretProvider:VaultUri`.** The default secret provider is **plaintext** — without Key Vault, upstream OIDC client secrets and TOTP / MFA seeds are stored in cleartext in Table Storage (and in backups). Configure Key Vault for any production deployment.
- **Lock down the admin API.** `AdminApi:Enabled` defaults to **true**. The admin scope (`AdminApi:Scope`, default `authagonal-admin`) grants full management and user impersonation. Network-restrict the `/api/v1/*` admin routes and tightly control who is issued the admin scope, or set `AdminApi:Enabled = false` if unused.
- **Protect internal endpoints.** Set `Cluster:Secret` so `/_internal/cluster/gossip` and `/_internal/backchannel-logout` require the `X-Cluster-Secret` header — especially when gossip is routed through a load balancer via `Cluster:InternalUrl`.
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
