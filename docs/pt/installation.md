---
layout: default
title: Instalação
locale: pt
---

# Instalação

## Docker (recomendado)

Baixe e execute a imagem pré-construída:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  authagonal
```

## Docker Compose

Para desenvolvimento local com Azurite (emulador do Azure Storage):

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

## Compilação a partir do Código-Fonte

### Pré-requisitos

- .NET 10 SDK
- Node.js 22+

### Compilar

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

### Build Docker

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como Biblioteca (NuGet)

Referencie os pacotes Authagonal no seu próprio projeto ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

Em seguida, componha-o no seu `Program.cs`:

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

Consulte [Extensibilidade](extensibility) para todos os pontos de extensão e [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) para um exemplo completo.

## SPA de Login (npm)

A interface de login é publicada como um pacote npm para personalização:

```bash
npm install @drawboard/authagonal-login
```

Personalize via `branding.json` (veja [Personalização Visual](branding)) e compile o SPA no `wwwroot/` do seu servidor:

```bash
cd node_modules/@drawboard/authagonal-login
npx vite build
cp -r dist/* /path/to/wwwroot/
```

## Ferramenta de Migração

Para migrar do Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migração](migration) para detalhes.
