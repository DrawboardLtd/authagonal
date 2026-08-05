---
layout: default
title: Configuración
locale: es
---

# Configuración

Authagonal se configura mediante `appsettings.json` o variables de entorno. Las variables de entorno usan `__` como separador de sección (por ejemplo, `Storage__ConnectionString`).

## Ajustes requeridos

El almacenamiento puede configurarse de dos maneras: proporcione **o bien** `Storage:ConnectionString` **o bien** `Storage:TableServiceUri` (la ruta de identidad administrada, preferida en producción).

| Ajuste | Variable de entorno | Descripción |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Cadena de conexión de Azure Table Storage con una clave de cuenta. Adecuada para desarrollo / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint de Table Storage con identidad administrada, por ejemplo `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` y **preferida en producción**: se autentica mediante `DefaultAzureCredential`, por lo que ninguna clave de acceso queda nunca en un secreto. El host debe otorgar a la identidad de la carga de trabajo el rol **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | La URL pública base de este servidor (por ejemplo, `https://auth.example.com`) |

## Almacenamiento

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(ninguno)* | Cadena de conexión con clave de cuenta (ver Ajustes requeridos). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(ninguno)* | URI de Table Storage con identidad administrada (ver Ajustes requeridos). Tiene prioridad sobre `Storage:ConnectionString` cuando ambos están establecidos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Si se deben mantener las tablas de índice de búsqueda por prefijo `UserFirstNames` / `UserLastNames` que respaldan la búsqueda de nombres por prefijo del administrador. Establezca `false` en hosts que no exponen la búsqueda de nombres del administrador para omitir esas escrituras. **Nota sobre escalabilidad:** estos índices usan una única partición caliente y limitan el rendimiento a aproximadamente 2.000 operaciones/seg a escala; deshabilítelos si no necesita la búsqueda por nombre. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base a la que el endpoint `/connect/authorize` redirige para la SPA de inicio de sesión (pantallas de inicio de sesión, step-up y consentimiento). Establezca esto cuando la interfaz de inicio de sesión se sirva desde un origen distinto al del servidor; el valor predeterminado es la ruta relativa `/login` servida por la SPA integrada. |

## Autenticación

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Duración de la sesión por cookie (deslizante) |
| `Authentication:AllowInsecureCookie` | `false` | Let the session cookie be sent over plain http (`SameAsRequest` instead of `Always`). **Development only** — see the English documentation. |
| `Authentication:CookieDomain` | *(unset)* | Scope the session cookie to a parent domain. **Costs the `__Host-` prefix and its origin binding** — see the English documentation. |
| `Auth:AllowInsecureHttp` | `false` | Permite que los endpoints OAuth (`/connect/*`) respondan a peticiones http en claro. **Solo para desarrollo.** RFC 6749 §3.1/§3.2 exigen TLS en los endpoints de autorización y de token, así que por defecto una petición que no sea https a cualquiera de ellos se rechaza con `invalid_request`. El esquema se evalúa *después* del procesamiento de las cabeceras reenviadas, de modo que un proxy que termina TLS y reenvía `X-Forwarded-Proto: https` pasa la barrera con esta opción desactivada — siempre que ese proxy esté declarado en `ForwardedHeaders:KnownNetworks` / `KnownProxies`; sin esa declaración la cabecera se ignora. Solo un despliegue genuinamente en texto claro (el `docker-compose.yml` incluido, la demo de servidor personalizado) la necesita, y el servidor registra una advertencia en el arranque siempre que esté activada. Se propaga a `AuthagonalProtocolOptions.AllowInsecureHttp`, por lo que también gobierna los endpoints que pertenecen a `Authagonal.Protocol` (ver [Extensibilidad](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:MaxFailedAttempts` | `5` | Intentos de inicio de sesión fallidos antes del bloqueo de cuenta |
| `Auth:LockoutDurationMinutes` | `10` | Duración del bloqueo de cuenta después del máximo de intentos fallidos |
| `Auth:MaxRegistrationsPerIp` | `5` | Registros máximos por dirección IP dentro de la ventana |
| `Auth:RegistrationWindowMinutes` | `60` | Ventana de limitación de velocidad de registro |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Máximo de correos de restablecimiento de contraseña por dirección de destino dentro de la ventana (basado en el correo, no en la IP del llamador, para que una dirección no pueda ser bombardeada con correos) |
| `Auth:PasswordResetWindowMinutes` | `60` | Ventana de limitación de velocidad de restablecimiento de contraseña |
| `Auth:AutoConfirmEmailDomains` | *(vacío)* | Dominios de correo (arreglo de cadenas) cuyos registros de autoservicio se confirman automáticamente: omiten el correo de verificación. Vacío (el valor predeterminado) significa que cada registro debe verificarse. Pensado solo para desarrollo/pruebas; nunca incluya un dominio que pueda recibir correo real. |
| `Auth:EmailVerificationExpiryHours` | `24` | Tiempo de vida del enlace de verificación de correo |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tiempo de vida del enlace de restablecimiento de contraseña |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tiempo de vida del token de verificación MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tiempo de vida del token de configuración MFA (para inscripción forzada) |
| `Auth:Pbkdf2Iterations` | `100000` | Número de iteraciones PBKDF2 para el hashing de contraseñas |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Suelo de tiempo de reloj al que se retiene un inicio de sesión fallido antes de devolver `invalid_credentials`, medido desde el comienzo de la petición. Cierra el oráculo temporal de enumeración de usuarios: una cuenta inexistente se verifica contra un hash ficticio en el formato PBKDF2 nativo, pero una cuenta real puede seguir teniendo un hash bcrypt o ASP.NET Identity V3 importado con un coste distinto, así que igualar el trabajo es imposible y lo que se impone es igualar el tiempo transcurrido. Súbalo por encima del hash más lento que tenga el despliegue, por ejemplo si importó bcrypt con coste superior a 11 o elevó `Pbkdf2Iterations` muy por encima del valor predeterminado: se registra una única advertencia la primera vez que un inicio de sesión fallido lo excede. `0` desactiva el relleno y vuelve a abrir el oráculo. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Ventana de gracia opcional (en segundos) para la reutilización concurrente del token de actualización. `0` (predeterminado) mantiene la postura estricta: cualquier reutilización de un token de actualización ya consumido revoca todos los tokens de ese usuario+cliente. Establezca `> 0` para tratar una reutilización dentro de la ventana como un reintento idempotente (vuelve a entregar los tokens sucesores), útil para clientes móviles con cortes de conectividad. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Habilita el endpoint de registro dinámico de clientes `POST /connect/register` (RFC 7591). Deshabilitado por defecto porque el registro abierto puede ser objeto de abuso en despliegues multi-tenant. Ver [Registro dinámico de clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Tiempo de vida de la clave de firma RSA antes de la rotación automática |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frecuencia de recarga de claves de firma desde el almacenamiento |
| `Auth:KeyRotationEnabled` | `false` | Habilita la rotación automática de claves de firma |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frecuencia con la que se comprueba si la clave activa necesita rotación |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotar cuando la clave activa expire dentro de esta cantidad de días |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificaciones del sello de seguridad del cookie |

## Data Protection

Las claves de Data Protection de ASP.NET Core (que cifran el cookie de sesión) deben compartirse entre instancias: ver [Escalabilidad](scaling#cookie-encryption-data-protection). Opciones de persistencia, en orden de precedencia:

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `DataProtection:BlobUri` | *(ninguno)* | URI de blob de Azure explícito para el conjunto de claves (por ejemplo, `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Se autentica mediante `DefaultAzureCredential`: la ruta preferida en producción junto con `Storage:TableServiceUri`. |
| *(alternativa)* | — | Cuando `DataProtection:BlobUri` no está establecido y `Storage:ConnectionString` apunta a una cuenta de almacenamiento real (no Azurite), las claves se persisten automáticamente en un contenedor `dataprotection` de esa cuenta. Con Azurite, las claves recurren al almacén predeterminado basado en archivos. |

En el backend de AWS, pase un cliente S3 + bucket a `AddAuthagonalAwsStorage` para persistir el conjunto de claves en S3: ver [Instalación → backend de AWS](installation#aws-backend).

## Cache y tiempos de espera

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Tiempo de cache de los orígenes CORS permitidos |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duración de cache del documento de descubrimiento OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duración de cache de los metadatos SAML del IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Tiempo de vida del parámetro state de autorización OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Tiempo de vida del ID AuthnRequest SAML (prevención de replay) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Tiempo de espera de la verificación de salud de Table Storage |

## Servicios en segundo plano

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Retraso inicial antes de la primera limpieza de tokens expirados |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalo de limpieza de tokens expirados |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Retraso inicial antes de la primera reconciliación de autorizaciones |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalo de reconciliación de autorizaciones |

## Roles

Los roles se definen en el arreglo `Roles` y se inyectan al inicio, junto con los clientes, scopes y
proveedores. Sembrarlos importa sobre todo cuando un scope está restringido con
[`AllowedRoles`](scopes#scopes-restringidos-por-rol): un scope restringido a un rol que nadie crea
queda restringido para todo el mundo, incluido el operador que lo configuró, y falla en silencio —
sencillamente nunca se concede.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Consola interna del personal",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Campo | Descripción |
|---|---|
| `Name` | El nombre del rol, tal como se usa en `Scope.AllowedRoles` y en el claim `roles` del token |
| `Description` | Legible para humanos; se actualiza en arranques posteriores cuando la semilla la indica |
| `Members` | Correos que se añaden al rol en cada arranque. Una dirección sin usuario todavía se omite con una advertencia y se reintenta en el siguiente arranque — el arranque nunca depende de una cuenta que alguien no ha creado |

La siembra es **aditiva e idempotente**. Nunca elimina un rol ni revoca una pertenencia: la
configuración no es la fuente de verdad sobre quién tiene qué, así que un rol concedido a través de
la API de administración sobrevive al siguiente reinicio.

## Clientes

Los clientes se definen en el arreglo `Clients` y se inyectan al inicio. Cada cliente puede tener:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Tipos de concesión

| Tipo de concesión | Caso de uso |
|---|---|
| `authorization_code` | Inicio de sesión interactivo del usuario (aplicaciones web, SPA, móvil) |
| `client_credentials` | Comunicación servicio a servicio |
| `refresh_token` | Renovación de token (requiere `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Concesión de autorización de dispositivo (RFC 8628) para dispositivos con entrada limitada |

### Uso del token de actualización

| Valor | Comportamiento |
|---|---|
| `OneTime` (predeterminado) | Cada actualización emite un nuevo token de actualización e invalida el anterior. De forma predeterminada (`Auth:RefreshTokenReuseGraceSeconds = 0`) cualquier reutilización de un token ya consumido revoca de inmediato todos los tokens de ese usuario+cliente; **no** hay ventana de gracia activada por defecto. Establezca `Auth:RefreshTokenReuseGraceSeconds` en un valor positivo para optar por una ventana de tolerancia a reintentos. |
| `ReUse` | El mismo token de actualización se reutiliza hasta su expiración. |

### Aplicaciones de aprovisionamiento

El arreglo `ProvisioningApps` referencia los identificadores de aplicaciones definidos en la sección de configuración `ProvisioningApps`. Cuando un usuario se autoriza a través de este cliente, se aprovisiona en esas aplicaciones mediante TCC. Ver [Aprovisionamiento](provisioning) para más detalles.

## Aplicaciones de aprovisionamiento

Defina las aplicaciones posteriores en las que los usuarios deben ser aprovisionados:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Ver [Aprovisionamiento](provisioning) para la especificación completa del protocolo TCC.

## Política de MFA

La autenticación multifactor se aplica por cliente mediante la propiedad `MfaPolicy`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (predeterminado) | Sin desafío MFA, incluso si el usuario tiene MFA inscrito |
| `Enabled` | Desafía a los usuarios que tienen MFA inscrito; no fuerza la inscripción |
| `Required` | Desafía a los usuarios inscritos; fuerza la inscripción para los usuarios sin MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Cuando `MfaPolicy` es `Required` y el usuario no ha inscrito MFA, el inicio de sesión devuelve `{ mfaSetupRequired: true, setupToken: "..." }`. El token de configuración autentica al usuario en los endpoints de configuración de MFA (mediante el encabezado `X-MFA-Setup-Token`) para que pueda inscribirse antes de obtener una sesión por cookie.

Los inicios de sesión federados (SAML/OIDC) también respetan la política de MFA: un usuario con MFA inscrito se enruta a través del desafío MFA después de que el IdP externo lo autentica, y `Required` fuerza la inscripción para los usuarios federados sin MFA.

### Anulación mediante IAuthHook

El método `IAuthHook.ResolveMfaPolicyAsync` puede anular la política del cliente por usuario:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Política de contraseñas

Personalice los requisitos de robustez de contraseñas:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propiedad | Predeterminado | Descripción |
|---|---|---|
| `MinLength` | `8` | Longitud mínima de la contraseña |
| `MinUniqueChars` | `2` | Número mínimo de caracteres distintos |
| `RequireUppercase` | `true` | Requerir al menos una letra mayúscula |
| `RequireLowercase` | `true` | Requerir al menos una letra minúscula |
| `RequireDigit` | `true` | Requerir al menos un dígito |
| `RequireSpecialChar` | `true` | Requerir al menos un carácter no alfanumérico |

La política se aplica durante el restablecimiento de contraseña y el registro de usuarios por el administrador. La interfaz de inicio de sesión obtiene la política activa desde `GET /api/auth/password-policy` para mostrar los requisitos dinámicamente.

## Proveedores SAML

Defina los proveedores de identidad SAML en la configuración. Estos se inyectan al inicio:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propiedad | Requerido | Descripción |
|---|---|---|
| `ConnectionId` | Sí | Identificador estable (usado en URLs como `/saml/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (predeterminado: ConnectionId) |
| `EntityId` | Sí | Identificador de entidad del SP **de este servidor**: el identificador que usted registra en el IdP, no el identificador de entidad propio del IdP |
| `MetadataLocation` | Sí | URL al XML de metadatos SAML del IdP |
| `AllowedDomains` | No | Dominios de correo electrónico enrutados a este proveedor vía SSO |

## Proveedores OIDC

Defina los proveedores de identidad OIDC en la configuración. Estos se inyectan al inicio:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propiedad | Requerido | Descripción |
|---|---|---|
| `ConnectionId` | Sí | Identificador estable (usado en URLs como `/oidc/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (predeterminado: ConnectionId) |
| `MetadataLocation` | Sí | URL al documento de descubrimiento OpenID Connect del IdP |
| `ClientId` | Sí | ID de cliente OAuth2 registrado con el IdP |
| `ClientSecret` | Sí | Secreto de cliente OAuth2 (protegido vía `ISecretProvider` al inicio) |
| `RedirectUrl` | No | **Se ignora.** La URI de redirección se deriva por solicitud como `{Issuer}/oidc/callback`: registre *esa* en el IdP. Un valor aquí no tiene efecto y se registra como ignorado. |
| `AllowedDomains` | No | Dominios de correo electrónico enrutados a este proveedor vía SSO |

> **Nota:** Los proveedores también pueden gestionarse en tiempo de ejecución mediante la [API de administración](admin-api). Los proveedores configurados se actualizan (upsert) en cada inicio, por lo que los cambios de configuración surten efecto al reiniciar.

## Proveedor de secretos

Los secretos de clientes OIDC upstream y las semillas TOTP / MFA pueden almacenarse en Azure Key Vault en lugar de en texto plano:

| Ajuste | Descripción |
|---|---|
| `SecretProvider:VaultUri` | URI del Key Vault (por ejemplo, `https://my-vault.vault.azure.net/`). Si no se establece, se usa el proveedor de **texto plano** y los secretos se almacenan tal cual en Table Storage. |

| `SecretProvider:RequireVaultReferences` | `false` de forma predeterminada. Cuando es `true`, una referencia almacenada sin prefijo de vault (`kv:` para Key Vault, `sm:` para AWS Secrets Manager) es un **error** en lugar de aceptarse como valor en texto plano. Actívelo una vez terminada la migración al vault. |

Cuando está configurado, los valores de secretos que parecen referencias de Key Vault se resuelven en tiempo de ejecución. Usa `DefaultAzureCredential` para la autenticación.

### Migrar a un vault, y cerrar la puerta después

Ambos proveedores respaldados por vault devuelven una referencia sin prefijo tal cual, tratándola como un valor en texto plano escrito antes de que el despliegue tuviera un vault. Eso es lo que permite migrar un sistema en marcha secreto a secreto en lugar de todo a la vez, pero dejado abierto es una vía de degradación permanente: cualquier cosa capaz de escribir una sola columna de configuración (una migración a medias, una ruta de administración que guarda un valor crudo donde corresponde una referencia, un atacante con acceso al almacenamiento pero no al vault) sustituye un secreto protegido por el vault por un valor de su elección, y verifica perfectamente, porque para una referencia sin prefijo la referencia *es* el valor.

Establezca `SecretProvider:RequireVaultReferences` cuando la migración haya terminado. Resolver una referencia sin prefijo lanzará entonces una excepción en lugar de devolver texto claro en silencio. Establecerlo mientras el proveedor resuelto es el de texto plano se rechaza en el arranque, ya que esa combinación no tiene ningún estado funcional: toda referencia que escribe el proveedor de texto plano carece de prefijo.

El servidor además registra una advertencia en el arranque siempre que un host que no es de desarrollo acaba con el proveedor de texto plano.

> ⚠️ **Producción: establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **texto plano**. Cuando `SecretProvider:VaultUri` no está establecido, los secretos de clientes OIDC upstream y las semillas TOTP / MFA se escriben en Azure Table Storage en texto claro, y por lo tanto aparecen en texto claro en cualquier [copia de seguridad](backup-restore). Para cualquier despliegue de producción, configure `SecretProvider:VaultUri` para que estos secretos se almacenen en Key Vault.

## API de administración

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Habilitada por defecto.** Establezca en `false` para deshabilitar todos los endpoints de administración (no se registrarán). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT requerido para acceder a los endpoints de administración. Cámbielo para que coincida con el nombre de su scope existente (por ejemplo, `projects-identity-admin` para migraciones de IdentityServer). |

> ⚠️ **La API de administración está habilitada por defecto y es altamente privilegiada.** El scope de administración otorga gestión completa y suplantación de usuarios: cualquiera que posea un token con `AdminApi:Scope` puede emitir tokens para cualquier usuario, gestionar clientes y leer/escribir toda la configuración. Restrinja por red los endpoints de administración (las rutas de administración `/api/v1/*`) y controle estrictamente a quién se le puede emitir el scope de administración. Como medida de defensa en profundidad, el scope está *reservado*: nunca puede otorgarse a un cliente OAuth (ver [API de administración](admin-api)) ni puede emitirse a través del endpoint de suplantación. Establezca `AdminApi:Enabled = false` por completo si la API de administración no se usa.

## Consentimiento

El consentimiento por cliente puede habilitarse con la propiedad `RequireConsent`:

| Valor | Comportamiento |
|---|---|
| `false` (predeterminado) | La autorización procede de inmediato después de la autenticación |
| `true` | Se muestra al usuario una pantalla de consentimiento con los scopes solicitados. El consentimiento se persiste durante 5 años y solo se vuelve a solicitar cuando se solicitan nuevos scopes. |

Los usuarios pueden ver y revocar sus otorgamientos de consentimiento en `GET /consent/grants` y `DELETE /consent/grants/{clientId}`.

## Cierre de sesión por canal trasero (Back-Channel Logout)

Registre un `BackChannelLogoutUri` en un cliente para recibir notificaciones de OIDC Back-Channel Logout 1.0. Cuando un usuario cierra sesión, Authagonal envía un token de cierre de sesión firmado (JWT) a la URI registrada de cada cliente.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Correo electrónico

El remitente de correo integrado usa [Resend](https://resend.com) y **se activa automáticamente** cuando `Email:ResendApiKey` está configurado: no se necesita registrar ningún servicio. Para usar un proveedor distinto, registre su propia implementación de `IEmailService` antes de llamar a `AddAuthagonal()` (tiene prioridad independientemente de las claves `Email:*`).

| Ajuste | Descripción |
|---|---|
| `Email:ResendApiKey` | Clave API de Resend. Cuando se establece, se usa el remitente Resend integrado. |
| `Email:SenderEmail` | Dirección de correo del remitente |
| `Email:SenderName` | Nombre para mostrar del remitente (predeterminado: `"Authagonal"`) |

> ⚠️ **Sin ningún remitente de correo, el autorregistro no funciona.** Cuando `Email:ResendApiKey` no está establecido y no hay ningún `IEmailService` personalizado registrado, un servicio no-op descarta silenciosamente todo el correo: los correos de verificación y de restablecimiento de contraseña nunca llegan, y como el inicio de sesión requiere un correo confirmado por defecto, los usuarios autorregistrados nunca pueden iniciar sesión. `UseAuthagonal` registra una advertencia al inicio en este estado. Válvula de escape para desarrollo/pruebas: `Auth:AutoConfirmEmailDomains` confirma automáticamente los registros de los dominios indicados.

Los correos a direcciones `@example.com` se omiten silenciosamente (útil para pruebas).

## Cluster

La capa de agrupación proporciona **elección de líder** (para que los trabajos con líder dedicado, como la rotación de claves de firma, se ejecuten en exactamente un nodo) y un **bus de eventos entre nodos**, detrás de backends conectables. El valor predeterminado es en proceso: un único nodo es siempre su propio líder, el ajuste adecuado para un solo nodo y para el desarrollo local, sin configuración alguna.

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal. Cuando es `false`, el nodo se ejecuta de forma independiente (siempre líder, bus de eventos en proceso). |
| `Cluster:Secret` | `Cluster__Secret` | *(ninguno)* | Secreto compartido requerido en el endpoint de uso interno `/_internal/backchannel-logout`. Cuando se establece, los llamadores deben presentarlo en el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Cuando **no** se establece, el endpoint solo es accesible desde IPs de origen de loopback / privadas (RFC 1918 / link-local / ULA); una solicitud externa que lleve una IP pública se rechaza. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Duración de la concesión de liderazgo. Se renueva aproximadamente cada mitad de este intervalo. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Frecuencia con la que el backend del bus de eventos sondea los mensajes publicados por otros nodos. |

Los **despliegues multinodo** intercambian un backend real mediante la devolución de llamada `configureClustering` en `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registran solo el bus de eventos, manteniendo la concesión en proceso, para nodos que deben recibir eventos del cluster pero nunca deben competir por el liderazgo.

Ver [Escalabilidad](scaling) para conocer cómo se comportan el liderazgo y el bus de eventos entre instancias.

## Encabezados reenviados (proxy de confianza)

Authagonal basa la limitación de velocidad y el bloqueo de cuenta en la IP del cliente, y solo emite HSTS en solicitudes HTTPS. Detrás de un proxy inverso / ingress, la IP real del cliente y el esquema llegan en los encabezados `X-Forwarded-For` / `X-Forwarded-Proto`. Estos ajustes controlan **qué saltos de proxy son de confianza** para establecer esos valores, de modo que un llamador no pueda falsificar `X-Forwarded-For` para suplantar la IP del cliente.

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Número de saltos de proxy a respetar desde la derecha de la cadena `X-Forwarded-For`. El valor predeterminado de `1` confía solo en el único salto que añade su ingress e ignora cualquier cosa más a la izquierda en la cadena. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (arreglo) | *(vacío)* | Rangos CIDR (arreglo de cadenas, por ejemplo `"10.0.0.0/8"`) autorizados a establecer los encabezados reenviados. Establézcalo en el CIDR de su proxy / ingress / pod. Declararlo es lo que permite que `X-Forwarded-Proto` se tenga en cuenta siquiera — véase más abajo. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (arreglo) | *(vacío)* | Direcciones IP de proxy individuales (arreglo de cadenas) autorizadas a establecer los encabezados reenviados. Use junto con o en lugar de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Las dos cabeceras no se confían en los mismos términos

`X-Forwarded-For` ajusta la **IP del cliente**: la clave de la que dependen la limitación de tasa, el bloqueo de cuentas y la guarda de `/_internal`. Sin nada declarado, Authagonal la acepta desde el bucle local y los rangos RFC1918, y registra una advertencia. Es un valor por defecto de mejor esfuerzo, y mejora el comportamiento del framework con una lista de confianza vacía, que consiste en aceptar la cabecera de *cualquier* llamante.

`X-Forwarded-Proto` cambia el **esquema**, y el esquema decide si `/connect/*` responde siquiera (RFC 6749 §3.1/§3.2), si las cookies se marcan como `Secure` y si las URL absolutas generadas son https. Se acepta **únicamente** desde un proxy que usted haya declarado en `KnownNetworks` / `KnownProxies`. Una dirección privada no es una declaración: Authagonal se distribuye como biblioteca y no puede ver la red en la que se ha desplegado, así que «el par tiene una dirección privada» es una conjetura sobre la topología. En una LAN plana, una VPC compartida o un puente de contenedores compartido, toda carga de trabajo vecina está dentro de esos rangos y podría afirmar `https` sobre una petición que llegó en claro.

**Si su proxy no tiene una dirección fija** — un ingress de Kubernetes, un balanceador rotatorio, una plataforma que no le dirá el CIDR del salto — declare como proxy a todos los pares:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Esto es seguro exactamente cuando nada salvo el proxy puede alcanzar el proceso, que es la suposición sobre la que ese despliegue ya se apoya. Dejarla por escrito la sitúa donde puede revisarse, en lugar de dejar que la biblioteca la infiera. Si otras cargas de trabajo *sí* pueden alcanzar Kestrel directamente, con este ajuste podrán suplantar el esquema y la IP del cliente: fije entonces el CIDR real.

> ⚠️ **Se requiere un proxy que termine TLS, y debe estar declarado.** Authagonal debe ejecutarse detrás de un proxy inverso que termine TLS (o terminar TLS él mismo). HSTS (`Strict-Transport-Security`) solo se emite en solicitudes HTTPS, y los endpoints OAuth rechazan de plano las peticiones en claro salvo que se active `Auth:AllowInsecureHttp` — de modo que el proxy debe reenviar `X-Forwarded-Proto: https` **y** figurar en `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` para que se envíe HSTS y `/connect/*` responda siquiera. No declarar nada es el fallo habitual al actualizar: la cabecera llega, nada está autorizado a aplicarla, y toda petición a `/connect/*` responde 400 en un despliegue que realmente está sobre TLS. El registro de arranque lo dice, y el cuerpo del rechazo también.

## Limitación de velocidad

Los límites de velocidad integrados protegen los endpoints propensos a abuso:

| Endpoint | Límite | Ventana | Basado en |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hora (`Auth:RegistrationWindowMinutes`) | IP del cliente |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | Correo de destino |
| `POST /connect/register` (cuando está habilitado) | 10 | 1 hora | IP del cliente |
| Endpoints SCIM | 200 | 1 minuto | Cliente SCIM |

Los límites se aplican **en proceso por nodo** (detrás del punto de extensión `IRateLimiter`), por lo que con N instancias el techo efectivo es N× el valor configurado. Trátelos como una red de seguridad y aplique el límite global autoritativo en el borde (WAF / ingress / CDN). Ver [Escalabilidad](scaling#rate-limiting).

## CORS

CORS se configura dinámicamente. Los orígenes de todos los `AllowedCorsOrigins` de los clientes registrados se permiten automáticamente, con un cache de 60 minutos.

## HashiCorp Vault Transit

Authagonal puede firmar JWTs usando el motor de secretos Transit de HashiCorp Vault. Las claves privadas nunca salen de Vault: solo la operación de firma se delega de forma remota. Las claves públicas se almacenan en cache localmente para la verificación.

Esto se configura programáticamente al alojarlo como biblioteca. Ver [Extensibilidad](extensibility) para más detalles.

## Ejemplo completo

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
