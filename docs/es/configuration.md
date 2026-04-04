---
layout: default
title: Configuracion
locale: es
---

# Configuracion

Authagonal se configura mediante `appsettings.json` o variables de entorno. Las variables de entorno usan `__` como separador de seccion (por ejemplo, `Storage__ConnectionString`).

## Ajustes requeridos

| Ajuste | Variable de entorno | Descripcion |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Cadena de conexion de Azure Table Storage |
| `Issuer` | `Issuer` | La URL publica base de este servidor (por ejemplo, `https://auth.example.com`) |

## Autenticacion

| Ajuste | Predeterminado | Descripcion |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Duracion de la sesion por cookie (deslizante) |
| `Auth:MaxFailedAttempts` | `5` | Intentos de inicio de sesion fallidos antes del bloqueo de cuenta |
| `Auth:LockoutDurationMinutes` | `10` | Duracion del bloqueo de cuenta despues del maximo de intentos fallidos |
| `Auth:MaxRegistrationsPerIp` | `5` | Registros maximos por direccion IP dentro de la ventana |
| `Auth:RegistrationWindowMinutes` | `60` | Ventana de limitacion de velocidad de registro |
| `Auth:EmailVerificationExpiryHours` | `24` | Tiempo de vida del enlace de verificacion de correo |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tiempo de vida del enlace de restablecimiento de contrasena |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tiempo de vida del token de verificacion MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tiempo de vida del token de configuracion MFA (para inscripcion forzada) |
| `Auth:Pbkdf2Iterations` | `100000` | Numero de iteraciones PBKDF2 para el hashing de contrasenas |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Ventana de gracia para la reutilizacion concurrente del token de actualizacion |
| `Auth:SigningKeyLifetimeDays` | `90` | Tiempo de vida de la clave de firma RSA antes de la rotacion automatica |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frecuencia de recarga de claves de firma desde el almacenamiento |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificaciones del sello de seguridad del cookie |

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

### Uso del token de actualizacion

| Valor | Comportamiento |
|---|---|
| `OneTime` (predeterminado) | Cada actualizacion emite un nuevo token de actualizacion. El anterior se invalida con una ventana de gracia de 60 segundos para solicitudes concurrentes. La reutilizacion despues de la ventana de gracia revoca todos los tokens para ese usuario+cliente. |
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

Los inicios de sesion federados (SAML/OIDC) omiten MFA -- el proveedor de identidad externo lo gestiona.

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
| `EntityId` | Si | Identificador de entidad del proveedor de servicios SAML |
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

Los secretos de clientes y proveedores OIDC pueden almacenarse opcionalmente en Azure Key Vault:

| Ajuste | Descripcion |
|---|---|
| `SecretProvider:VaultUri` | URI del Key Vault (por ejemplo, `https://my-vault.vault.azure.net/`). Si no se establece, los secretos se tratan como texto plano. |

Cuando esta configurado, los valores de secretos que parecen referencias de Key Vault se resuelven en tiempo de ejecucion. Usa `DefaultAzureCredential` para la autenticacion.

## Correo electronico

Por defecto, Authagonal usa un servicio de email no-op que descarta silenciosamente todos los emails. Para habilitar el envio de emails, registre una implementacion de `IEmailService` antes de llamar a `AddAuthagonal()`. El servicio integrado `EmailService` usa SendGrid.

| Ajuste | Descripcion |
|---|---|
| `Email:SendGridApiKey` | Clave API de SendGrid para enviar correos |
| `Email:FromAddress` | Direccion de correo del remitente |
| `Email:FromName` | Nombre para mostrar del remitente |
| `Email:VerificationTemplateId` | ID de plantilla dinamica de SendGrid para verificacion de correo |
| `Email:PasswordResetTemplateId` | ID de plantilla dinamica de SendGrid para restablecimiento de contrasena |

Los correos a direcciones `@example.com` se omiten silenciosamente (util para pruebas).

## Cluster

Las instancias de Authagonal forman automaticamente un cluster para compartir el estado de los limites de velocidad. La agrupacion esta habilitada por defecto sin necesidad de configuracion.

| Ajuste | Variable de entorno | Predeterminado | Descripcion |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal para la agrupacion. Establezca en `false` para limites de velocidad solo locales. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Grupo de multicast UDP para descubrimiento de pares |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Puerto de multicast UDP para descubrimiento de pares |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(ninguno)* | URL con balanceo de carga como alternativa para gossip cuando el multicast no esta disponible |
| `Cluster:Secret` | `Cluster__Secret` | *(ninguno)* | Secreto compartido para la autenticacion del endpoint de gossip (recomendado cuando se establece `InternalUrl`) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Frecuencia con la que las instancias intercambian el estado de limites de velocidad |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Frecuencia con la que las instancias se anuncian mediante multicast |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Descartar pares de los que no se ha recibido respuesta despues de esta cantidad de segundos |

**Sin configuracion (predeterminado):** Las instancias se descubren entre si mediante multicast UDP. Funciona en Kubernetes, Docker Compose o cualquier red compartida.

**Multicast deshabilitado (por ejemplo, algunas VPC en la nube):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Agrupacion completamente deshabilitada:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Ver [Escalabilidad](scaling) para mas detalles sobre como funciona la limitacion de velocidad distribuida.

## Limitacion de velocidad

Limites de velocidad integrados por IP aplicados en todas las instancias mediante el protocolo de gossip del cluster:

| Endpoint | Limite | Ventana |
|---|---|---|
| `POST /api/auth/register` | 5 registros | 1 hora |

Cuando la agrupacion esta habilitada, estos limites se consolidan entre todas las instancias. Cuando esta deshabilitada, cada instancia aplica su propio limite de forma independiente.

## CORS

CORS se configura dinamicamente. Los origenes de todos los `AllowedCorsOrigins` de los clientes registrados se permiten automaticamente, con un cache de 60 minutos.

## Ejemplo completo

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
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
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
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
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
