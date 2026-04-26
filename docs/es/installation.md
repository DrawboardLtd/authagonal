---
layout: default
title: Instalacion
locale: es
---

# Instalacion

## Docker (recomendado)

Descargue y ejecute la imagen preconstruida:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  authagonal
```

## Docker Compose

Para desarrollo local con Azurite (emulador de Azure Storage):

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

## Compilacion desde el codigo fuente

### Requisitos previos

- .NET 10 SDK
- Node.js 22+

### Compilacion

```bash
# Compilar todo
dotnet build

# Compilar la SPA de inicio de sesion
cd login-app
npm ci
npm run build

# Ejecutar el servidor
dotnet run --project src/Authagonal.Server
```

### Compilacion Docker

```bash
# Imagen del servidor (multi-etapa: compila la SPA + .NET en una sola imagen)
docker build -t authagonal .

# Herramienta de migracion
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como biblioteca (NuGet)

Referencie los paquetes de Authagonal en su propio proyecto ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

Luego integrelo en su `Program.cs`:

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

Consulte [Extensibilidad](extensibility) para todos los puntos de sustitucion y [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) para un ejemplo completo.

## SPA de inicio de sesion (npm)

La interfaz de inicio de sesion se publica como un paquete npm para personalizacion:

```bash
npm install @authagonal/login
```

El paquete incluye JS y CSS compilados — importe componentes y estilos directamente en su propia aplicacion React. Consulte [Servidor personalizado](custom-server) para una guia completa.

## Herramienta de migracion

Para migrar desde Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migracion](migration) para mas detalles.
