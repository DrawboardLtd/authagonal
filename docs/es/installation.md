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
  drawboardci/authagonal
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
- Node.js 24+

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
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

El paquete del proveedor de almacenamiento es intercambiable: `Authagonal.AzureProvider` para Azure Table Storage (la integracion predeterminada de `AddAuthagonal()`), o `Authagonal.AwsProvider` para DynamoDB / S3 / Secrets Manager -- ver [Backend AWS](#aws-backend) mas abajo.

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

### Email

El remitente integrado de [Resend](https://resend.com) se activa automaticamente cuando `Email:ResendApiKey` y `Email:SenderEmail` estan configurados: no requiere registro de servicio. Sin ningun `IEmailService`, los correos de verificacion y de restablecimiento de contrasena se **descartan en silencio**, y como el inicio de sesion requiere un correo confirmado de forma predeterminada, los usuarios autoregistrados nunca podran iniciar sesion (`UseAuthagonal` registra una advertencia en el arranque). Configure las claves `Email:*`, registre su propio `IEmailService` antes de `AddAuthagonal()`, o incluya sus dominios en `Auth:AutoConfirmEmailDomains` para omitir la verificacion (solo desarrollo/pruebas). Ver [Configuracion → Email](configuration#email).

## AWS backend

Para ejecutar en AWS en lugar de Azure, referencie `Authagonal.AwsProvider` y registre el paquete de AWS **antes** de `AddAuthagonal()`: esos registros son los que hacen que `AddAuthagonal()` omita su integracion de Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Las tablas de DynamoDB replican el diseno de Azure una a una y se garantizan en el arranque (idempotente: sin efecto cuando ya estan aprovisionadas por Terraform). Las credenciales se resuelven mediante la cadena estandar de AWS (env / rol de instancia EC2 / IRSA), por lo que no hay una division entre cadena de conexion e identidad administrada: no se necesita ninguna configuracion `Storage:*`.

> ⚠️ **Claves de DataProtection en S3.** Sin un cliente S3 + bucket, el conjunto de claves de Data Protection de ASP.NET Core se mantiene en memoria: aceptable para un unico nodo en desarrollo, pero las cookies y los tokens antiforgery se rompen al reiniciar y entre nodos en produccion. Pase siempre el cliente S3 y el bucket para un despliegue de produccion en AWS.

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
- **Proteja los endpoints internos.** Establezca `Cluster:Secret` para que el endpoint interno `/_internal/backchannel-logout` requiera el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Cuando no se establece, solo acepta IPs de origen de loopback / privadas (RFC 1918 / link-local / ULA): asegurese de que la confianza de sus forwarded-headers este configurada para que un llamante externo no pueda aparentar ser interno.
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
