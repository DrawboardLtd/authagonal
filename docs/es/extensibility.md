---
layout: default
title: Extensibilidad
locale: es
---

# Extensibilidad

Authagonal puede alojarse como una biblioteca en su propio proyecto ASP.NET Core, con control total sobre las implementaciones de servicios.

## Metodos de extension

Tres metodos componen Authagonal en cualquier aplicacion ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Alojamiento multi-tenant

Para despliegues multi-tenant, use `AddAuthagonalCore()` en su lugar. Registra endpoints, middleware y servicios principales, pero omite el almacenamiento y los servicios en segundo plano; usted los proporciona por tenant. La gestion de claves de firma usa de forma predeterminada el singleton `ProtocolKeyManager` de `Authagonal.Protocol`, y un host que registra su propio `IKeyManager` antes de `AddAuthagonalCore()` lo conserva:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` y las interfaces de almacenamiento (`IClientStore`, `IScimTokenStore`, etc.) se resuelven desde `HttpContext.RequestServices` en tiempo de solicitud, por lo que los registros con ambito (scoped) funcionan correctamente para el aislamiento por tenant.

## Sustitucion de servicios

Registre sus implementaciones personalizadas **antes** de llamar a `AddAuthagonal()`. Authagonal usa `TryAdd` internamente, por lo que sus registros tienen prioridad:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` es especial: es una tuberia de registro multiple. Registre tantos hooks como desee (cualquier tiempo de vida, incluido `AddScoped`) y todos se ejecutan en orden de registro. El `NullAuthHook` sin efecto se agrega solo cuando no se ha registrado ningun hook para cuando se ejecutan `AddAuthagonal()` / `AddAuthagonalCore()`, por lo que registre siempre sus hooks primero.

### Puntos de extensibilidad

| Interfaz | Predeterminado | Proposito |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sin efecto, se agrega solo cuando no hay ningun hook registrado) | Hooks de ciclo de vida para eventos de autenticacion: registro de auditoria, validacion personalizada, webhooks. Se pueden registrar varios hooks; todos se ejecutan en orden |
| `IEmailService` | `NullEmailService` (no-op), o el emisor Resend integrado cuando `Email:ResendApiKey` esta configurado | Entrega de correos electronicos para verificacion, restablecimiento de contrasena y avisos de cuenta existente |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Aprovisionamiento de usuarios en aplicaciones posteriores |
| `ISecretProvider` | `PlaintextSecretProvider`, o el `KeyVaultSecretProvider` integrado cuando `SecretProvider:VaultUri` esta configurado | Almacenamiento reversible de secretos (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lee desde `IConfiguration`) | Resolucion de tenant para despliegues multi-tenant |
| `IKeyManager` | `ProtocolKeyManager` (singleton, de `Authagonal.Protocol`) | Gestion de claves de firma; anular para aislamiento de claves por tenant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Resuelve las aplicaciones de aprovisionamiento disponibles; anular para resolucion dinamica o por tenant |
| `IAuditLogger` | `NullAuditLogger` (no-op) | Registro de auditoria para cambios de configuracion y eventos relevantes para la seguridad |

Otros tres puntos de extension viven a **nivel de almacen** en lugar de en la DI: `IFieldCipher`, `IIndexTokenizer` e `IChangeWriter` (todos en `Authagonal.Core.Services`). Los proveedores de almacenamiento los aceptan como parametros de constructor opcionales; vea sus secciones a continuacion.

## IAuthHook

La interfaz `IAuthHook` proporciona hooks en el ciclo de vida de la autenticacion. Los metodos en la ruta critica (autenticacion, creacion de usuarios, emision de tokens) pueden lanzar una excepcion para cancelar la operacion; los metodos mas recientes son notificaciones posteriores al hecho. Se pueden registrar varias implementaciones de `IAuthHook` y todas se ejecutan en orden de registro.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parametros

| Metodo | Notas y valores de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (los hosts pueden pasar el suyo propio, por ejemplo un origen SCIM) |
| `OnUserDeletedAsync` | `"admin"`; solo notificacion, es posible que el registro ya no sea legible |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Tipos de concesion: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Se llama despues de la verificacion de la contrasena; devuelve la politica MFA efectiva para el usuario. Predeterminado: devolver `clientPolicy` sin cambios. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Los mismos metodos que `OnMfaVerifiedAsync`. Se dispara solo despues de credenciales validas de primer factor, por lo que las rafagas son una fuerte senal de intento de omision de MFA (distinta de `OnLoginFailedAsync`, la etapa de contrasena) |
| `OnEmailConfirmedAsync` | El usuario confirmo su correo electronico mediante el enlace de verificacion; ya persistido |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; la credencial ya esta activa |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` es true cuando la eliminacion no dejo ningun factor primario |
| `OnRecoveryCodesRegeneratedAsync` | El conjunto anterior de codigos de recuperacion queda invalidado |
| `OnPasswordChangedAsync` | por ejemplo `"reset"`; el cambio se persiste y las sesiones existentes se invalidan |

### Ejemplo: Registro de auditoria

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Ejemplo: Restriccion de dominio

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## ISecretProvider

`ISecretProvider` (en `Authagonal.Core.Services`) es el punto de extension de cifrado reversible para secretos almacenados como los secretos de cliente SSO, las contrasenas SMTP y las semillas TOTP. `ProtectAsync` convierte un texto plano en una referencia que el almacen persiste; `ResolveAsync` convierte la referencia de vuelta en el texto plano. El `PlaintextSecretProvider` predeterminado almacena los valores tal cual (la referencia ES el valor).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Establecer `SecretProvider:VaultUri` conecta automaticamente el `KeyVaultSecretProvider` integrado (Azure Key Vault mediante `DefaultAzureCredential`). Para cualquier otra cosa, registre su propia implementacion antes de `AddAuthagonal()`.

## Cifrado de campos PII: IFieldCipher

`IFieldCipher` cifra los valores de campos de PII de usuario individuales (telefono, empresa, atributos personalizados, correo electronico y nombres en la fila de perfil) en reposo. Es un punto de extension a nivel de almacen: los proveedores de almacenamiento lo toman como un parametro de constructor opcional (por ejemplo, `TableUserStore`), y cuando esta ausente se aplica el `NullFieldCipher` de paso directo, por lo que el cifrado es estrictamente opcional y los hosts sin configurar siguen almacenando texto plano.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Dos puntos del contrato importan. `ProtectAsync` debe devolver un token de texto cifrado autodescriptivo (por ejemplo, el `vault:v{n}:...` de Vault Transit), y `ResolveAsync` debe dejar pasar sin cambios un valor que no reconozca como su propio texto cifrado. La regla de paso directo es lo que permite implementar el cifrado de forma diferida sobre las filas existentes: una lectura de una fila no migrada devuelve el texto plano heredado, y la siguiente escritura lo vuelve a proteger.

## Busqueda con indice ciego: IIndexTokenizer

`IIndexTokenizer` mantiene los campos cifrados como aptos para busqueda. Convierte un valor de texto plano normalizado en un token de indice ciego determinista y seguro como clave de tabla, tipicamente un HMAC con clave donde la clave vive fuera de la base de datos. El determinismo significa que una busqueda por igualdad sigue funcionando ("email = x" se convierte en "token = HMAC(x)"), mientras que un volcado de la base de datos no puede ni recomputar ni revertir un token. La busqueda por prefijo se superpone tokenizando cada prefijo de un valor por separado, ya que un HMAC con clave destruye el orden y los escaneos por rango.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Al igual que `IFieldCipher`, es un parametro de constructor de almacen opcional con un valor predeterminado de paso directo (`NullIndexTokenizer`), por lo que las filas de indice se mantienen con clave en texto plano hasta que usted lo active. Los tokens devueltos deben ser seguros como valores de PartitionKey/RowKey de Azure Table (ninguno de `/ \ # ?` ni caracteres de control).

## Captura de registro de cambios: IChangeWriter

`IChangeWriter` (renombrado desde `ITombstoneWriter` en 0.6.0) registra la clave de cada fila modificada en una tabla de registro de cambios dedicada, de modo que las copias de seguridad incrementales puedan encontrar lo que cambio sin escanear la columna `Timestamp` no indexada de las tablas en vivo. Las eliminaciones se capturan para cada tabla (un escaneo de filas en vivo no puede ver una fila que ya no existe); las inserciones/actualizaciones (upserts) se capturan para las tablas que la copia de seguridad lee del registro en lugar de escanear. Implementaciones integradas: `TableChangeWriter` (Azure Table Storage) y `DynamoChangeWriter` (DynamoDB).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrato de orden para implementadores y llamadores: escriba la lapida (tombstone) de eliminacion ANTES de eliminar la fila de datos. Un fallo en el orden inverso pierde la eliminacion de todas las copias de seguridad futuras, ya que las eliminaciones son la unica clase de mutacion que un reescaneo no puede autocorregir. El fallo inverso es seguro: una escritura posterior a la clave vuelve a estampar una marca de tiempo mas nueva, y la fusion/restauracion conservan las filas escritas despues de la lapida.

## Endpoints personalizados

Agregue sus propios endpoints junto a los de Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Integracion con HashiCorp Vault Transit

Authagonal puede delegar la firma de JWT en el motor de secretos Transit de HashiCorp Vault. Las claves privadas nunca salen de Vault; solo la operacion de firma es remota. Las claves publicas se almacenan en cache localmente para la verificacion.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Vault Transit HTTP client
builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

// Register Vault Transit services
builder.Services.AddSingleton<VaultTransitClient>();
builder.Services.AddSingleton<VaultTransitCryptoProvider>();

builder.Services.AddAuthagonal(builder.Configuration);
```

El `VaultTransitClient` proporciona estas operaciones:

| Metodo | Descripcion |
|---|---|
| `SignAsync(keyName, data)` | Firma datos usando una clave Transit de Vault |
| `VerifyAsync(keyName, data, signature)` | Verifica una firma serializada como JWS mediante el endpoint de verificacion de Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Cifrado simetrico bajo una clave `aes256-gcm96`; devuelve tokens `vault:v{n}:...` para almacenar tal cual |
| `HmacAsync` / `HmacBatchAsync` | HMAC con clave bajo una clave `hmac` (tokens de indice ciego) |
| `CreateKeyAsync(keyName, type)` | Crea una nueva clave Transit (predeterminado: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garantiza de forma idempotente que una clave existe con el tipo deseado (la recrea si el tipo no coincide; las claves Transit no se pueden reescribir de tipo en el lugar) |
| `RotateKeyAsync(keyName)` | Rota una clave a una nueva version |
| `DeleteKeyAsync(keyName)` | Elimina una clave (habilita `deletion_allowed` primero) |
| `ReadKeyAsync(keyName)` | Lee los metadatos, las versiones y las claves publicas de la clave |
| `KeyExistsAsync(keyName)` | Comprueba si una clave existe |

El `VaultTransitCryptoProvider` se integra con el `JsonWebTokenHandler` de .NET para que la firma de JWT use Vault de forma transparente. El `VaultTransitSecurityKey` y el `VaultTransitSignatureProvider` gestionan la integracion de bajo nivel.

## Correo electronico

El emisor Resend integrado se activa automaticamente cuando `Email:ResendApiKey` esta configurado (establezca tambien `Email:SenderEmail`). Sin ningun `IEmailService`, el correo se descarta mediante `NullEmailService`, y como la puerta de inicio de sesion de correo confirmado esta activada de forma predeterminada, los usuarios que se registran por si mismos nunca podrian iniciar sesion; `UseAuthagonal()` registra una advertencia de inicio ruidosa en ese estado.

Para usar otro proveedor, registre su propio `IEmailService` antes de `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` tambien declara `SendAccountExistsEmailAsync` (enviado cuando alguien intenta registrar un correo ya registrado, manteniendo la respuesta de registro neutral frente a la enumeracion de cuentas). Tiene una implementacion predeterminada sin efecto (no-op), por lo que las implementaciones existentes siguen compilando.

## Ver tambien

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): ejemplo completo funcional
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): ejemplo de aplicacion cliente
