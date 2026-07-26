---
layout: default
title: API de administración
locale: es
---

# API de administración

Los endpoints de administración requieren un token de acceso JWT con el scope `authagonal-admin` (configurable vía `AdminApi:Scope`).

Todos los endpoints están bajo `/api/v1/`.

## Arranque del primer token de administración

Cada endpoint `/api/v1/*` requiere un token bearer que porte el scope de administración, pero la propia API de administración (y el [registro dinámico de clientes](client-registration)) **se niega a crear o actualizar cualquier cliente que posea ese scope** (`403 forbidden_scope`), por lo que un cliente creado en tiempo de ejecución nunca puede escalar a administrador. La única forma de emitir un token de administración es un **cliente sembrado por configuración**: las entradas de la sección de configuración `Clients:` son insertadas o actualizadas al inicio por `ClientSeedService`, y la configuración es de confianza: la protección de scope prohibido solo se aplica a las APIs en tiempo de ejecución.

Siembre un cliente `client_credentials` con el scope de administración en `appsettings.json` (o las variables de entorno / almacén de secretos equivalentes):

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` se hashea al inicio; proporcione `SecretHashes` en su lugar si prefiere mantener solo un valor pre-hasheado en la configuración. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` se aceptan como alias de `Id`/`Name`/`GrantTypes`/`Scopes`.)

Luego intercambie las credenciales por un token en el endpoint de token estándar:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

La concesión `client_credentials` valida el scope solicitado contra los `AllowedScopes` del cliente, dado que el cliente sembrado posee `authagonal-admin`, se emite el token. Úselo como `Authorization: Bearer {access_token}` en cada llamada de administración:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Mantenga el secreto del cliente sembrado en el almacén de secretos de su despliegue; rotarlo es un cambio de configuración más un reinicio.

## Usuarios

### Obtener usuario

```
GET /api/v1/profile/{userId}
```

Devuelve los detalles del usuario, incluyendo los vínculos de inicio de sesión externo.

### El usuario existe

```
GET /api/v1/profile/{userId}/exists
```

Devuelve `204` si el usuario existe, `404` en caso contrario (una comprobación económica de existencia, sin cuerpo).

### Registrar usuario

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crea un usuario y envía un correo de verificación. Devuelve `409 user_exists` si el correo ya está en uso.

Campos opcionales solo para administradores: `userId` (id proporcionado por el llamador; `409 user_id_in_use` en caso de colisión), `emailConfirmed` (crea el usuario ya verificado, omitiendo el correo de verificación), `companyName`, `organizationId`, `phone`, `locale`, y `customAttributes` (un mapa de cadenas persistido en el usuario y reenviado a los destinos de aprovisionamiento).

`skipProvisioning: true` crea la identidad sin ejecutar el aprovisionamiento. Está pensado para una
aplicación de primera parte que ES ELLA MISMA un destino de aprovisionamiento y que ya está a mitad
de configurar a este usuario: llama aquí para acuñar la identidad, no para que se le devuelva la
llamada sobre un usuario que está creando en ese mismo momento. Sin esta opción, esa aplicación
recibe su propio Try para un usuario a medio construir, con solo los atributos que sobrevivieron al
viaje de ida y vuelta — y, si se recupera, acaba aprovisionando al usuario dos veces.

### Actualizar usuario

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` es requerido; todos los demás campos son opcionales: solo los campos proporcionados se actualizan. Cambiar `organizationId` desencadena:
- Rotación del SecurityStamp (invalida todas las sesiones por cookie dentro de 30 minutos)
- Revocación de todos los tokens de actualización

### Eliminar usuario

```
DELETE /api/v1/profile/{userId}
```

Elimina al usuario, revoca todos los otorgamientos y desaprovisiona de todas las aplicaciones posteriores (mejor esfuerzo).

### Confirmar correo electrónico

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar correo de verificación

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Vincular identidad externa

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Desvincular identidad externa

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestión de MFA

### Obtener estado de MFA

```
GET /api/v1/profile/{userId}/mfa
```

Devuelve el estado de MFA y los métodos inscritos de un usuario.

### Restablecer todo MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Elimina todas las credenciales MFA y establece `MfaEnabled=false`. El usuario deberá volver a inscribirse si es requerido.

### Eliminar credencial MFA específica

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Elimina una credencial MFA específica (por ejemplo, un autenticador perdido). Si se elimina el último método primario, MFA se desactiva.

## Proveedores SSO

### Proveedores SAML

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial — only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

La creación requiere `connectionName`, `entityId`, y **exactamente uno de** `metadataLocation` (una URL de metadatos) o `metadataXml` (metadatos del IdP pegados, para IdPs sin una URL de metadatos; se validan al analizarse y se condensan al guardar). Opcional: `nameIdFormat` (omítalo para el valor predeterminado emailAddress, `"none"` para omitir NameIDPolicy, recomendado para ADFS, o una URN de formato NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Cada conexión obtiene un par de claves SP generado por el servidor; nunca lo devuelve la API. Ver [SAML](saml) para más detalles.

### Proveedores OIDC

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

La creación requiere `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Opcional: `iconUrl`, `allowedDomains`, `passthroughParams`. El secreto del cliente se protege en reposo y nunca se devuelve. Ver [Federación OIDC](oidc-federation).

### Dominios SSO

```
GET    /api/v1/sso/domains                 # List all
```

## Clientes

Gestione los clientes OAuth en tiempo de ejecución. Todas las rutas requieren la política `IdentityAdmin` (el scope de administración).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Crear / actualizar cliente

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` devuelve `409` si el cliente ya existe. `PUT` actualiza un cliente existente (`404` si no se encuentra); en una actualización, solo los scopes recién añadidos se comprueban contra escalada de privilegios.

Notas:

- **Los hashes de secretos nunca se devuelven.** `clientSecretHashes` se elimina de cada respuesta (listar, obtener, crear, actualizar). En una actualización, omitir `clientSecretHashes` conserva el secreto almacenado; proporcionar nuevos hashes lo rota.
- **El scope de administración no puede otorgarse a un cliente.** Solicitar `AdminApi:Scope` (predeterminado `authagonal-admin`) en `allowedScopes` devuelve `403 forbidden_scope`: ningún cliente puede poseer el scope de administración, de lo contrario un cliente `client_credentials` podría emitir tokens de administración indefinidamente.
- Añadir scopes que el llamador no está autorizado a otorgar devuelve `403`.

## Scopes

Gestione scopes OAuth personalizados en tiempo de ejecución. Ver [Scopes de OAuth](scopes) para el modelo completo de scopes.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Devuelve `201` al crear (`409` si el scope ya existe), el JSON del scope al obtener/actualizar, y `204` al eliminar.

## Aplicaciones de aprovisionamiento

Gestione los destinos de aprovisionamiento posteriores en tiempo de ejecución. Todas las rutas requieren la política `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Crear / actualizar aplicación de aprovisionamiento

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` y `callbackUrl` son requeridos; `callbackUrl` debe ser una URL `http(s)` absoluta.
- `tryTimeoutSeconds` se limita al rango 5–300.
- **La clave API nunca se devuelve.** Las respuestas exponen `hasApiKey` (un booleano) en lugar de la clave en sí. En una actualización, omitir `apiKey` la deja sin cambios, una cadena vacía la borra, y un valor la reemplaza.
- La creación está sujeta a una cuota configurable por despliegue (`IProvisioningAppQuota`); excederla devuelve `400 provisioning_app_limit`. La respuesta de listado incluye el `limit` actual.

### Probar una aplicación de aprovisionamiento

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envía un `POST {callbackUrl}/try` sintético con una carga útil de ejemplo (y la clave API de la app como token bearer si está establecida) y devuelve `{ success, statusCode, body }` para que pueda verificar la conectividad desde la interfaz de administración.

## Roles

### Listar roles

```
GET /api/v1/roles
```

### Obtener rol

```
GET /api/v1/roles/{roleId}
```

### Crear rol

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Actualizar rol

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Eliminar rol

```
DELETE /api/v1/roles/{roleId}
```

### Asignar rol a usuario

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

La asignación es por **nombre de rol**, no por id de rol. Devuelve la lista de roles actualizada del usuario.

### Desasignar rol de usuario

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Obtener roles de un usuario

```
GET /api/v1/roles/user/{userId}
```

## Tokens SCIM

### Generar token

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` y `expiresInDays` son opcionales (omita `expiresInDays` para un token que no expira). Devuelve el token en texto plano una sola vez. Almacénelo de forma segura: no se puede recuperar de nuevo.

### Listar tokens

```
GET /api/v1/scim/tokens?clientId=client-id
```

Devuelve los metadatos del token (ID, fecha de creación) sin el valor del token en texto plano.

### Revocar token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens

### Suplantar usuario

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emite tokens (de acceso, de actualización y, cuando se solicita `openid`, token de identidad) en nombre de un usuario sin requerir sus credenciales. Útil para pruebas y soporte. Los parámetros se pasan como cadenas de consulta.

| Parámetro de consulta | Requerido | Descripción |
|---|---|---|
| `clientId` | Sí | El cliente para el que se emiten los tokens. Los tiempos de vida de los tokens provienen de la configuración de este cliente. |
| `userId` | Sí | El usuario a suplantar. |
| `scopes` | No | Lista de scopes **separados por espacios** (codifique los espacios en la URL). Por defecto, los `AllowedScopes` del cliente cuando se omite. |

Restricciones:

- Los scopes están limitados a los `AllowedScopes` del cliente: solicitar cualquier scope que el propio cliente no podría solicitar devuelve `400 invalid_scope`.
- El scope de administración (`AdminApi:Scope`, predeterminado `authagonal-admin`) **no** puede emitirse a través de este endpoint; solicitarlo devuelve `403 forbidden_scope`. Esto evita que un token de administración (posiblemente de tiempo limitado) emita un token de acceso/actualización de administración de larga duración.

La respuesta es una respuesta de token estándar con `access_token`, `refresh_token`, opcionalmente `id_token`, `expires_in` y el `scope` otorgado (separado por espacios).
