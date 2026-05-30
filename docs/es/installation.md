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

## Lista de verificacion de seguridad para produccion

Antes de exponer Authagonal a trafico real, confirme lo siguiente. Cada elemento se detalla en la pagina de [Configuracion](configuration).

- **Ejecute detras de un proxy que termine TLS.** Authagonal debe situarse detras de un proxy inverso / ingress que termine TLS. El cookie de sesion usa `SecurePolicy = SameAsRequest` y HSTS solo se emite en HTTPS, por lo que el proxy debe reenviar `X-Forwarded-Proto: https`. Establezca `ForwardedHeaders:KnownNetworks` (o `KnownProxies`) con el CIDR de su ingress / pod para que la IP del cliente y el esquema no puedan ser suplantados; `ForwardedHeaders:ForwardLimit` tiene el valor predeterminado `1` (confiar solo en el ultimo salto).
- **Establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **texto plano**: sin Key Vault, los secretos de clientes OIDC upstream y las semillas TOTP / MFA se almacenan en texto claro en Table Storage (y en las copias de seguridad). Configure Key Vault para cualquier despliegue de produccion.
- **Restrinja la API de administracion.** `AdminApi:Enabled` tiene el valor predeterminado **true**. El scope de administracion (`AdminApi:Scope`, predeterminado `authagonal-admin`) otorga gestion completa y suplantacion de usuarios. Restrinja por red las rutas de administracion `/api/v1/*` y controle estrictamente a quien se le emite el scope de administracion, o establezca `AdminApi:Enabled = false` si no se usa.
- **Proteja los endpoints internos.** Establezca `Cluster:Secret` para que `/_internal/cluster/gossip` y `/_internal/backchannel-logout` requieran el encabezado `X-Cluster-Secret`, especialmente cuando el gossip se enruta a traves de un balanceador de carga mediante `Cluster:InternalUrl`.
- **Cifre las copias de seguridad.** Con el proveedor de secretos de texto plano, las copias de seguridad contienen secretos. La tabla `SigningKeys` se excluye de las copias de seguridad de forma predeterminada; si opta por incluirla mediante `Backup:IncludeSigningKeys`, el destino de la copia de seguridad debe estar cifrado en reposo. Ver [Copia de seguridad y restauracion](backup-restore).

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
