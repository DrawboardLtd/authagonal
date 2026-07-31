---
layout: default
title: Instalación
locale: es
---

# Instalación

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

## Compilación desde el código fuente

### Requisitos previos

- .NET 10 SDK
- Node.js 24+

### Compilación

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

### Compilación Docker

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como biblioteca (NuGet)

Referencie los paquetes de Authagonal en su propio proyecto ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

El paquete del proveedor de almacenamiento es intercambiable: `Authagonal.AzureProvider` para Azure Table Storage (la integración predeterminada de `AddAuthagonal()`), `Authagonal.SqlProvider` para PostgreSQL o SQLite autoalojados (ver [Backend SQL](#sql-backend)), o `Authagonal.AwsProvider` para DynamoDB / S3 / Secrets Manager (ver [Backend AWS](#aws-backend)).

Luego intégrelo en su `Program.cs`:

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

Consulte [Extensibilidad](extensibility) para todos los puntos de sustitución y [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) para un ejemplo completo.

### Email

El remitente integrado de [Resend](https://resend.com) se activa automáticamente cuando `Email:ResendApiKey` y `Email:SenderEmail` están configurados: no requiere registro de servicio. Sin ningún `IEmailService`, los correos de verificación y de restablecimiento de contraseña se **descartan en silencio**, y como el inicio de sesión requiere un correo confirmado de forma predeterminada, los usuarios autoregistrados nunca podrán iniciar sesión (`UseAuthagonal` registra una advertencia en el arranque). Configure las claves `Email:*`, registre su propio `IEmailService` antes de `AddAuthagonal()`, o incluya sus dominios en `Auth:AutoConfirmEmailDomains` para omitir la verificación (solo desarrollo/pruebas). Ver [Configuración → Email](configuration#email).

## SQL backend

Para ejecutar sobre su propia base de datos en lugar de un servicio en la nube, referencie `Authagonal.SqlProvider` y regístrelo **antes** de `AddAuthagonal()`: esos registros son los que hacen que `AddAuthagonal()` omita su integración de Azure Table Storage:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Las tablas replican uno a uno los diseños de Azure y DynamoDB, y se crean en el arranque si no existen (cada sentencia es un `IF NOT EXISTS`, por lo que es seguro que varios pods compitan por crearlas y no hace nada contra un esquema que usted mismo haya aprovisionado). No se necesita configuración `Storage:*`. El conjunto de claves de Data Protection se persiste en la misma base de datos, así que las cookies y los tokens antiforgery sobreviven a los reinicios y funcionan entre pods sin ningún servicio adicional.

SQLite serializa los escritores, por lo que es un backend de un solo nodo: el lease en proceso y el bus de eventos de clúster registrados de forma predeterminada son la combinación correcta ahí. Un despliegue de PostgreSQL con varios pods querrá `clustering.UseSql(dataSource)` para la elección de líder.

> **Intercalación (collation).** En PostgreSQL las columnas de clave se fijan a `COLLATE "C"`. El esquema de claves es ordinal por bytes en todo momento (límites de prefijo, rangos de partición por entorno, el barrido de expiración de concesiones, la paginación por keyset), y una base de datos creada con una intercalación lingüística -- `en_US.UTF-8` y las locales ICU son los valores predeterminados habituales -- ordenaría la puntuación y las mayúsculas de otra forma y devolvería en silencio las filas equivocadas. La fijación hace que el diseño sea independiente de cómo se creó la base de datos; no necesita crearla de ninguna manera concreta.

Consulte el [README del paquete](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) para el diseño de las tablas, las primitivas de concurrencia detrás de cada garantía de un solo uso, y cómo añadir un dialecto para otro motor.

## AWS backend

Para ejecutar en AWS en lugar de Azure, referencie `Authagonal.AwsProvider` y registre el paquete de AWS **antes** de `AddAuthagonal()`: esos registros son los que hacen que `AddAuthagonal()` omita su integración de Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Las tablas de DynamoDB replican el diseño de Azure una a una y se garantizan en el arranque (idempotente: sin efecto cuando ya están aprovisionadas por Terraform). Las credenciales se resuelven mediante la cadena estándar de AWS (env / rol de instancia EC2 / IRSA), por lo que no hay una división entre cadena de conexión e identidad administrada: no se necesita ninguna configuración `Storage:*`.

> ⚠️ **Claves de DataProtection en S3.** Sin un cliente S3 + bucket, el conjunto de claves de Data Protection de ASP.NET Core se mantiene en memoria: aceptable para un único nodo en desarrollo, pero las cookies y los tokens antiforgery se rompen al reiniciar y entre nodos en producción. Pase siempre el cliente S3 y el bucket para un despliegue de producción en AWS.

## SPA de inicio de sesión (npm)

La interfaz de inicio de sesión se publica como un paquete npm para personalización:

```bash
npm install @authagonal/login
```

El paquete incluye JS y CSS compilados: importe componentes y estilos directamente en su propia aplicación React. Consulte [Servidor personalizado](custom-server) para una guía completa.

## Lista de verificación de seguridad para producción

Antes de exponer Authagonal a tráfico real, confirme lo siguiente. Cada elemento se detalla en la página de [Configuración](configuration).

- **Ejecute detrás de un proxy que termine TLS, y declárelo.** Authagonal debe situarse detrás de un proxy inverso / ingress que termine TLS (o terminar TLS él mismo). HSTS solo se emite en HTTPS y `/connect/*` rechaza el texto en claro, por lo que el proxy debe reenviar `X-Forwarded-Proto: https` — y esa cabecera se ignora salvo que establezca `ForwardedHeaders:KnownNetworks` (o `KnownProxies`) con el CIDR o la dirección de su proxy. Use `["0.0.0.0/0", "::/0"]` si el proxy no tiene dirección fija y nada más puede alcanzar el proceso. `ForwardedHeaders:ForwardLimit` tiene el valor predeterminado `1` (confiar solo en el último salto).
- **Establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **texto plano**: sin Key Vault, los secretos de clientes OIDC upstream y las semillas TOTP / MFA se almacenan en texto claro en Table Storage (y en las copias de seguridad). Configure Key Vault para cualquier despliegue de producción.
- **Restrinja la API de administración.** `AdminApi:Enabled` tiene el valor predeterminado **true**. El scope de administración (`AdminApi:Scope`, predeterminado `authagonal-admin`) otorga gestión completa y suplantación de usuarios. Restrinja por red las rutas de administración `/api/v1/*` y controle estrictamente a quién se le emite el scope de administración, o establezca `AdminApi:Enabled = false` si no se usa.
- **Proteja los endpoints internos.** Establezca `Cluster:Secret` para que el endpoint interno `/_internal/backchannel-logout` requiera el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Cuando no se establece, solo acepta IPs de origen de loopback / privadas (RFC 1918 / link-local / ULA): asegúrese de que la confianza de sus forwarded-headers esté configurada para que un llamante externo no pueda aparentar ser interno.
- **Cifre las copias de seguridad.** Con el proveedor de secretos de texto plano, las copias de seguridad contienen secretos. La tabla `SigningKeys` se excluye de las copias de seguridad de forma predeterminada; si opta por incluirla mediante `Backup:IncludeSigningKeys`, el destino de la copia de seguridad debe estar cifrado en reposo. Ver [Copia de seguridad y restauración](backup-restore).

## Herramienta de migración

Para migrar desde Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migración](migration) para más detalles.
