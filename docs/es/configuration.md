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

El servicio de correo electronico predeterminado usa SendGrid. Para usar otro proveedor, implemente `IEmailService` y registrelo antes de `AddAuthagonal()` -- ver [Extensibilidad](extensibility).

| Ajuste | Descripcion |
|---|---|
| `Email:SendGridApiKey` | Clave API de SendGrid para enviar correos |
| `Email:FromAddress` | Direccion de correo del remitente |
| `Email:FromName` | Nombre para mostrar del remitente |
| `Email:VerificationTemplateId` | ID de plantilla dinamica de SendGrid para verificacion de correo |
| `Email:PasswordResetTemplateId` | ID de plantilla dinamica de SendGrid para restablecimiento de contrasena |

Los correos a direcciones `@example.com` se omiten silenciosamente (util para pruebas).

## Limitacion de velocidad

Limites de velocidad integrados por IP:

| Grupo de endpoints | Limite | Ventana |
|---|---|---|
| Endpoints de autenticacion (inicio de sesion, SSO) | 20 solicitudes | 1 minuto |
| Endpoint de token | 30 solicitudes | 1 minuto |

## CORS

CORS se configura dinamicamente. Los origenes de todos los `AllowedCorsOrigins` de los clientes registrados se permiten automaticamente, con un cache de 60 minutos.

## Ejemplo completo

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
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
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
