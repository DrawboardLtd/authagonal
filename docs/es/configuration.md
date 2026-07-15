---
layout: default
title: Configuracion
locale: es
---

# Configuracion

Authagonal se configura mediante `appsettings.json` o variables de entorno. Las variables de entorno usan `__` como separador de seccion (por ejemplo, `Storage__ConnectionString`).

## Ajustes requeridos

El almacenamiento puede configurarse de dos maneras: proporcione **o bien** `Storage:ConnectionString` **o bien** `Storage:TableServiceUri` (la ruta de identidad administrada, preferida en produccion).

| Ajuste | Variable de entorno | Descripcion |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Cadena de conexion de Azure Table Storage con una clave de cuenta. Adecuada para desarrollo / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint de Table Storage con identidad administrada, por ejemplo `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` y **preferida en produccion**: se autentica mediante `DefaultAzureCredential`, por lo que ninguna clave de acceso queda nunca en un secreto. El host debe otorgar a la identidad de la carga de trabajo el rol **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | La URL publica base de este servidor (por ejemplo, `https://auth.example.com`) |

## Almacenamiento

| Ajuste | Variable de entorno | Predeterminado | Descripcion |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(ninguno)* | Cadena de conexion con clave de cuenta (ver Ajustes requeridos). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(ninguno)* | URI de Table Storage con identidad administrada (ver Ajustes requeridos). Tiene prioridad sobre `Storage:ConnectionString` cuando ambos estan establecidos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Si se deben mantener las tablas de indice de busqueda por prefijo `UserFirstNames` / `UserLastNames` que respaldan la busqueda de nombres por prefijo del administrador. Establezca `false` en hosts que no exponen la busqueda de nombres del administrador para omitir esas escrituras. **Nota sobre escalabilidad:** estos indices usan una unica particion caliente y limitan el rendimiento a aproximadamente 2.000 operaciones/seg a escala; deshabilitelos si no necesita la busqueda por nombre. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base a la que el endpoint `/connect/authorize` redirige para la SPA de inicio de sesion (pantallas de inicio de sesion, step-up y consentimiento). Establezca esto cuando la interfaz de inicio de sesion se sirva desde un origen distinto al del servidor; el valor predeterminado es la ruta relativa `/login` servida por la SPA integrada. |

## Autenticacion

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Duracion de la sesion por cookie (deslizante) |
| `Authentication:AlwaysSecureCookie` | `false` | Fuerza incondicionalmente el atributo `Secure` del cookie de sesion. El valor predeterminado (`SameAsRequest`) ya produce un cookie Secure detras de un proxy que termina TLS y reenvia `X-Forwarded-Proto: https`. |
| `Auth:MaxFailedAttempts` | `5` | Intentos de inicio de sesion fallidos antes del bloqueo de cuenta |
| `Auth:LockoutDurationMinutes` | `10` | Duracion del bloqueo de cuenta despues del maximo de intentos fallidos |
| `Auth:MaxRegistrationsPerIp` | `5` | Registros maximos por direccion IP dentro de la ventana |
| `Auth:RegistrationWindowMinutes` | `60` | Ventana de limitacion de velocidad de registro |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Maximo de correos de restablecimiento de contrasena por direccion de destino dentro de la ventana (basado en el correo, no en la IP del llamador, para que una direccion no pueda ser bombardeada con correos) |
| `Auth:PasswordResetWindowMinutes` | `60` | Ventana de limitacion de velocidad de restablecimiento de contrasena |
| `Auth:AutoConfirmEmailDomains` | *(vacio)* | Dominios de correo (arreglo de cadenas) cuyos registros de autoservicio se confirman automaticamente: omiten el correo de verificacion. Vacio (el valor predeterminado) significa que cada registro debe verificarse. Pensado solo para desarrollo/pruebas; nunca incluya un dominio que pueda recibir correo real. |
| `Auth:EmailVerificationExpiryHours` | `24` | Tiempo de vida del enlace de verificacion de correo |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tiempo de vida del enlace de restablecimiento de contrasena |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tiempo de vida del token de verificacion MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tiempo de vida del token de configuracion MFA (para inscripcion forzada) |
| `Auth:Pbkdf2Iterations` | `100000` | Numero de iteraciones PBKDF2 para el hashing de contrasenas |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Ventana de gracia opcional (en segundos) para la reutilizacion concurrente del token de actualizacion. `0` (predeterminado) mantiene la postura estricta: cualquier reutilizacion de un token de actualizacion ya consumido revoca todos los tokens de ese usuario+cliente. Establezca `> 0` para tratar una reutilizacion dentro de la ventana como un reintento idempotente (vuelve a entregar los tokens sucesores), util para clientes moviles con cortes de conectividad. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Habilita el endpoint de registro dinamico de clientes `POST /connect/register` (RFC 7591). Deshabilitado por defecto porque el registro abierto puede ser objeto de abuso en despliegues multi-tenant. Ver [Registro dinamico de clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Tiempo de vida de la clave de firma RSA antes de la rotacion automatica |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frecuencia de recarga de claves de firma desde el almacenamiento |
| `Auth:KeyRotationEnabled` | `false` | Habilita la rotacion automatica de claves de firma |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frecuencia con la que se comprueba si la clave activa necesita rotacion |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotar cuando la clave activa expire dentro de esta cantidad de dias |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificaciones del sello de seguridad del cookie |

## Data Protection

Las claves de Data Protection de ASP.NET Core (que cifran el cookie de sesion) deben compartirse entre instancias: ver [Escalabilidad](scaling#cookie-encryption-data-protection). Opciones de persistencia, en orden de precedencia:

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `DataProtection:BlobUri` | *(ninguno)* | URI de blob de Azure explicito para el conjunto de claves (por ejemplo, `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Se autentica mediante `DefaultAzureCredential`: la ruta preferida en produccion junto con `Storage:TableServiceUri`. |
| *(alternativa)* | — | Cuando `DataProtection:BlobUri` no esta establecido y `Storage:ConnectionString` apunta a una cuenta de almacenamiento real (no Azurite), las claves se persisten automaticamente en un contenedor `dataprotection` de esa cuenta. Con Azurite, las claves recurren al almacen predeterminado basado en archivos. |

En el backend de AWS, pase un cliente S3 + bucket a `AddAuthagonalAwsStorage` para persistir el conjunto de claves en S3: ver [Instalacion → backend de AWS](installation#aws-backend).

## Cache y tiempos de espera

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Tiempo de cache de los origenes CORS permitidos |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duracion de cache del documento de descubrimiento OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duracion de cache de los metadatos SAML del IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Tiempo de vida del parametro state de autorizacion OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Tiempo de vida del ID AuthnRequest SAML (prevencion de replay) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Tiempo de espera de la verificacion de salud de Table Storage |

## Servicios en segundo plano

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Retraso inicial antes de la primera limpieza de tokens expirados |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalo de limpieza de tokens expirados |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Retraso inicial antes de la primera reconciliacion de autorizaciones |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalo de reconciliacion de autorizaciones |

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

### Tipos de concesion

| Tipo de concesion | Caso de uso |
|---|---|
| `authorization_code` | Inicio de sesion interactivo del usuario (aplicaciones web, SPA, movil) |
| `client_credentials` | Comunicacion servicio a servicio |
| `refresh_token` | Renovacion de token (requiere `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Concesion de autorizacion de dispositivo (RFC 8628) para dispositivos con entrada limitada |

### Uso del token de actualizacion

| Valor | Comportamiento |
|---|---|
| `OneTime` (predeterminado) | Cada actualizacion emite un nuevo token de actualizacion e invalida el anterior. De forma predeterminada (`Auth:RefreshTokenReuseGraceSeconds = 0`) cualquier reutilizacion de un token ya consumido revoca de inmediato todos los tokens de ese usuario+cliente; **no** hay ventana de gracia activada por defecto. Establezca `Auth:RefreshTokenReuseGraceSeconds` en un valor positivo para optar por una ventana de tolerancia a reintentos. |
| `ReUse` | El mismo token de actualizacion se reutiliza hasta su expiracion. |

### Aplicaciones de aprovisionamiento

El arreglo `ProvisioningApps` referencia los identificadores de aplicaciones definidos en la seccion de configuracion `ProvisioningApps`. Cuando un usuario se autoriza a traves de este cliente, se aprovisiona en esas aplicaciones mediante TCC. Ver [Aprovisionamiento](provisioning) para mas detalles.

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

Ver [Aprovisionamiento](provisioning) para la especificacion completa del protocolo TCC.

## Politica de MFA

La autenticacion multifactor se aplica por cliente mediante la propiedad `MfaPolicy`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (predeterminado) | Sin desafio MFA, incluso si el usuario tiene MFA inscrito |
| `Enabled` | Desafia a los usuarios que tienen MFA inscrito; no fuerza la inscripcion |
| `Required` | Desafia a los usuarios inscritos; fuerza la inscripcion para los usuarios sin MFA |

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

Cuando `MfaPolicy` es `Required` y el usuario no ha inscrito MFA, el inicio de sesion devuelve `{ mfaSetupRequired: true, setupToken: "..." }`. El token de configuracion autentica al usuario en los endpoints de configuracion de MFA (mediante el encabezado `X-MFA-Setup-Token`) para que pueda inscribirse antes de obtener una sesion por cookie.

Los inicios de sesion federados (SAML/OIDC) tambien respetan la politica de MFA: un usuario con MFA inscrito se enruta a traves del desafio MFA despues de que el IdP externo lo autentica, y `Required` fuerza la inscripcion para los usuarios federados sin MFA.

### Anulacion mediante IAuthHook

El metodo `IAuthHook.ResolveMfaPolicyAsync` puede anular la politica del cliente por usuario:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Forzar MFA para usuarios administradores independientemente de la configuracion del cliente
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Politica de contrasenas

Personalice los requisitos de robustez de contrasenas:

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

| Propiedad | Predeterminado | Descripcion |
|---|---|---|
| `MinLength` | `8` | Longitud minima de la contrasena |
| `MinUniqueChars` | `2` | Numero minimo de caracteres distintos |
| `RequireUppercase` | `true` | Requerir al menos una letra mayuscula |
| `RequireLowercase` | `true` | Requerir al menos una letra minuscula |
| `RequireDigit` | `true` | Requerir al menos un digito |
| `RequireSpecialChar` | `true` | Requerir al menos un caracter no alfanumerico |

La politica se aplica durante el restablecimiento de contrasena y el registro de usuarios por el administrador. La interfaz de inicio de sesion obtiene la politica activa desde `GET /api/auth/password-policy` para mostrar los requisitos dinamicamente.

## Proveedores SAML

Defina los proveedores de identidad SAML en la configuracion. Estos se inyectan al inicio:

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

| Propiedad | Requerido | Descripcion |
|---|---|---|
| `ConnectionId` | Si | Identificador estable (usado en URLs como `/saml/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (predeterminado: ConnectionId) |
| `EntityId` | Si | Identificador de entidad del SP **de este servidor**: el identificador que usted registra en el IdP, no el identificador de entidad propio del IdP |
| `MetadataLocation` | Si | URL al XML de metadatos SAML del IdP |
| `AllowedDomains` | No | Dominios de correo electronico enrutados a este proveedor via SSO |

## Proveedores OIDC

Defina los proveedores de identidad OIDC en la configuracion. Estos se inyectan al inicio:

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

| Propiedad | Requerido | Descripcion |
|---|---|---|
| `ConnectionId` | Si | Identificador estable (usado en URLs como `/oidc/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (predeterminado: ConnectionId) |
| `MetadataLocation` | Si | URL al documento de descubrimiento OpenID Connect del IdP |
| `ClientId` | Si | ID de cliente OAuth2 registrado con el IdP |
| `ClientSecret` | Si | Secreto de cliente OAuth2 (protegido via `ISecretProvider` al inicio) |
| `RedirectUrl` | Si | URI de redireccion OAuth2 registrada con el IdP |
| `AllowedDomains` | No | Dominios de correo electronico enrutados a este proveedor via SSO |

> **Nota:** Los proveedores tambien pueden gestionarse en tiempo de ejecucion mediante la [API de administracion](admin-api). Los proveedores configurados se actualizan (upsert) en cada inicio, por lo que los cambios de configuracion surten efecto al reiniciar.

## Proveedor de secretos

Los secretos de clientes OIDC upstream y las semillas TOTP / MFA pueden almacenarse en Azure Key Vault en lugar de en texto plano:

| Ajuste | Descripcion |
|---|---|
| `SecretProvider:VaultUri` | URI del Key Vault (por ejemplo, `https://my-vault.vault.azure.net/`). Si no se establece, se usa el proveedor de **texto plano** y los secretos se almacenan tal cual en Table Storage. |

Cuando esta configurado, los valores de secretos que parecen referencias de Key Vault se resuelven en tiempo de ejecucion. Usa `DefaultAzureCredential` para la autenticacion.

> ⚠️ **Produccion: establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **texto plano**. Cuando `SecretProvider:VaultUri` no esta establecido, los secretos de clientes OIDC upstream y las semillas TOTP / MFA se escriben en Azure Table Storage en texto claro, y por lo tanto aparecen en texto claro en cualquier [copia de seguridad](backup-restore). Para cualquier despliegue de produccion, configure `SecretProvider:VaultUri` para que estos secretos se almacenen en Key Vault.

## API de administracion

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Habilitada por defecto.** Establezca en `false` para deshabilitar todos los endpoints de administracion (no se registraran). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT requerido para acceder a los endpoints de administracion. Cambielo para que coincida con el nombre de su scope existente (por ejemplo, `projects-identity-admin` para migraciones de IdentityServer). |

> ⚠️ **La API de administracion esta habilitada por defecto y es altamente privilegiada.** El scope de administracion otorga gestion completa y suplantacion de usuarios: cualquiera que posea un token con `AdminApi:Scope` puede emitir tokens para cualquier usuario, gestionar clientes y leer/escribir toda la configuracion. Restrinja por red los endpoints de administracion (las rutas de administracion `/api/v1/*`) y controle estrictamente a quien se le puede emitir el scope de administracion. Como medida de defensa en profundidad, el scope esta *reservado*: nunca puede otorgarse a un cliente OAuth (ver [API de administracion](admin-api)) ni puede emitirse a traves del endpoint de suplantacion. Establezca `AdminApi:Enabled = false` por completo si la API de administracion no se usa.

## Consentimiento

El consentimiento por cliente puede habilitarse con la propiedad `RequireConsent`:

| Valor | Comportamiento |
|---|---|
| `false` (predeterminado) | La autorizacion procede de inmediato despues de la autenticacion |
| `true` | Se muestra al usuario una pantalla de consentimiento con los scopes solicitados. El consentimiento se persiste durante 5 anos y solo se vuelve a solicitar cuando se solicitan nuevos scopes. |

Los usuarios pueden ver y revocar sus otorgamientos de consentimiento en `GET /consent/grants` y `DELETE /consent/grants/{clientId}`.

## Cierre de sesion por canal trasero (Back-Channel Logout)

Registre un `BackChannelLogoutUri` en un cliente para recibir notificaciones de OIDC Back-Channel Logout 1.0. Cuando un usuario cierra sesion, Authagonal envia un token de cierre de sesion firmado (JWT) a la URI registrada de cada cliente.

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

## Correo electronico

El remitente de correo integrado usa [Resend](https://resend.com) y **se activa automaticamente** cuando `Email:ResendApiKey` esta configurado: no se necesita registrar ningun servicio. Para usar un proveedor distinto, registre su propia implementacion de `IEmailService` antes de llamar a `AddAuthagonal()` (tiene prioridad independientemente de las claves `Email:*`).

| Ajuste | Descripcion |
|---|---|
| `Email:ResendApiKey` | Clave API de Resend. Cuando se establece, se usa el remitente Resend integrado. |
| `Email:SenderEmail` | Direccion de correo del remitente |
| `Email:SenderName` | Nombre para mostrar del remitente (predeterminado: `"Authagonal"`) |

> ⚠️ **Sin ningun remitente de correo, el autorregistro no funciona.** Cuando `Email:ResendApiKey` no esta establecido y no hay ningun `IEmailService` personalizado registrado, un servicio no-op descarta silenciosamente todo el correo: los correos de verificacion y de restablecimiento de contrasena nunca llegan, y como el inicio de sesion requiere un correo confirmado por defecto, los usuarios autorregistrados nunca pueden iniciar sesion. `UseAuthagonal` registra una advertencia al inicio en este estado. Valvula de escape para desarrollo/pruebas: `Auth:AutoConfirmEmailDomains` confirma automaticamente los registros de los dominios indicados.

Los correos a direcciones `@example.com` se omiten silenciosamente (util para pruebas).

## Cluster

La capa de agrupacion proporciona **eleccion de lider** (para que los trabajos con lider dedicado, como la rotacion de claves de firma, se ejecuten en exactamente un nodo) y un **bus de eventos entre nodos**, detras de backends conectables. El valor predeterminado es en proceso: un unico nodo es siempre su propio lider, el ajuste adecuado para un solo nodo y para el desarrollo local, sin configuracion alguna.

| Ajuste | Variable de entorno | Predeterminado | Descripcion |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal. Cuando es `false`, el nodo se ejecuta de forma independiente (siempre lider, bus de eventos en proceso). |
| `Cluster:Secret` | `Cluster__Secret` | *(ninguno)* | Secreto compartido requerido en el endpoint de uso interno `/_internal/backchannel-logout`. Cuando se establece, los llamadores deben presentarlo en el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Cuando **no** se establece, el endpoint solo es accesible desde IPs de origen de loopback / privadas (RFC 1918 / link-local / ULA); una solicitud externa que lleve una IP publica se rechaza. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Duracion de la concesion de liderazgo. Se renueva aproximadamente cada mitad de este intervalo. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Frecuencia con la que el backend del bus de eventos sondea los mensajes publicados por otros nodos. |

Los **despliegues multinodo** intercambian un backend real mediante la devolucion de llamada `configureClustering` en `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: liderazgo mediante una concesion de blob, bus de eventos mediante un registro en tabla (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// Equivalente en AWS (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` registran solo el bus de eventos, manteniendo la concesion en proceso, para nodos que deben recibir eventos del cluster pero nunca deben competir por el liderazgo.

Ver [Escalabilidad](scaling) para conocer como se comportan el liderazgo y el bus de eventos entre instancias.

## Encabezados reenviados (proxy de confianza)

Authagonal basa la limitacion de velocidad y el bloqueo de cuenta en la IP del cliente, y solo emite HSTS en solicitudes HTTPS. Detras de un proxy inverso / ingress, la IP real del cliente y el esquema llegan en los encabezados `X-Forwarded-For` / `X-Forwarded-Proto`. Estos ajustes controlan **que saltos de proxy son de confianza** para establecer esos valores, de modo que un llamador no pueda falsificar `X-Forwarded-For` para suplantar la IP del cliente.

| Ajuste | Variable de entorno | Predeterminado | Descripcion |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Numero de saltos de proxy a respetar desde la derecha de la cadena `X-Forwarded-For`. El valor predeterminado de `1` confia solo en el unico salto que anade su ingress e ignora cualquier cosa mas a la izquierda en la cadena. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (arreglo) | *(vacio)* | Rangos CIDR (arreglo de cadenas, por ejemplo `"10.0.0.0/8"`) autorizados a establecer los encabezados reenviados. **Garantia mas solida:** establezca esto en el CIDR de su ingress / pod para que solo esa red pueda establecer la IP del cliente. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (arreglo) | *(vacio)* | Direcciones IP de proxy individuales (arreglo de cadenas) autorizadas a establecer los encabezados reenviados. Use junto con o en lugar de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

> ⚠️ **Se requiere un proxy que termine TLS.** Authagonal debe ejecutarse detras de un proxy inverso que termine TLS. El cookie de sesion usa `SecurePolicy = SameAsRequest` y HSTS (`Strict-Transport-Security`) solo se emite en solicitudes HTTPS, por lo que el proxy debe reenviar `X-Forwarded-Proto: https` para que los cookies se marquen como `Secure` y se envie HSTS. Configure `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` con su proxy de confianza para que el esquema y la IP del cliente no puedan ser suplantados.

## Limitacion de velocidad

Los limites de velocidad integrados protegen los endpoints propensos a abuso:

| Endpoint | Limite | Ventana | Basado en |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hora (`Auth:RegistrationWindowMinutes`) | IP del cliente |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | Correo de destino |
| `POST /connect/register` (cuando esta habilitado) | 10 | 1 hora | IP del cliente |
| Endpoints SCIM | 200 | 1 minuto | Cliente SCIM |

Los limites se aplican **en proceso por nodo** (detras del punto de extension `IRateLimiter`), por lo que con N instancias el techo efectivo es N× el valor configurado. Tratelos como una red de seguridad y aplique el limite global autoritativo en el borde (WAF / ingress / CDN). Ver [Escalabilidad](scaling#rate-limiting).

## CORS

CORS se configura dinamicamente. Los origenes de todos los `AllowedCorsOrigins` de los clientes registrados se permiten automaticamente, con un cache de 60 minutos.

## HashiCorp Vault Transit

Authagonal puede firmar JWTs usando el motor de secretos Transit de HashiCorp Vault. Las claves privadas nunca salen de Vault: solo la operacion de firma se delega de forma remota. Las claves publicas se almacenan en cache localmente para la verificacion.

Esto se configura programaticamente al alojarlo como biblioteca. Ver [Extensibilidad](extensibility) para mas detalles.

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
