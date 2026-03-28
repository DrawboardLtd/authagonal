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
