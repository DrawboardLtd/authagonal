---
layout: default
title: API de administracion
locale: es
---

# API de administracion

Los endpoints de administracion requieren un token de acceso JWT con el scope `authagonal-admin` (configurable via `AdminApi:Scope`).

Todos los endpoints estan bajo `/api/v1/`.

## Arranque del primer token de administracion

Cada endpoint `/api/v1/*` requiere un token bearer que porte el scope de administracion -- pero la propia API de administracion (y el [registro dinamico de clientes](client-registration)) **se niega a crear o actualizar cualquier cliente que posea ese scope** (`403 forbidden_scope`), por lo que un cliente creado en tiempo de ejecucion nunca puede escalar a administrador. La unica forma de emitir un token de administracion es un **cliente sembrado por configuracion**: las entradas de la seccion de configuracion `Clients:` son insertadas o actualizadas al inicio por `ClientSeedService`, y la configuracion es de confianza -- la proteccion de scope prohibido solo se aplica a las APIs en tiempo de ejecucion.

Siembre un cliente `client_credentials` con el scope de administracion en `appsettings.json` (o las variables de entorno / almacen de secretos equivalentes):

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

(`ClientSecret` se hashea al inicio; proporcione `SecretHashes` en su lugar si prefiere mantener solo un valor pre-hasheado en la configuracion. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` se aceptan como alias de `Id`/`Name`/`GrantTypes`/`Scopes`.)

Luego intercambie las credenciales por un token en el endpoint de token estandar:

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

La concesion `client_credentials` valida el scope solicitado contra los `AllowedScopes` del cliente -- dado que el cliente sembrado posee `authagonal-admin`, se emite el token. Uselo como `Authorization: Bearer {access_token}` en cada llamada de administracion:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Mantenga el secreto del cliente sembrado en el almacen de secretos de su despliegue; rotarlo es un cambio de configuracion mas un reinicio.

## Usuarios

### Obtener usuario

```
GET /api/v1/profile/{userId}
```

Devuelve los detalles del usuario, incluyendo los vinculos de inicio de sesion externo.

### El usuario existe

```
GET /api/v1/profile/{userId}/exists
```

Devuelve `204` si el usuario existe, `404` en caso contrario (una comprobacion economica de existencia -- sin cuerpo).

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

Crea un usuario y envia un correo de verificacion. Devuelve `409 user_exists` si el correo ya esta en uso.

Campos opcionales solo para administradores: `userId` (id proporcionado por el llamador -- `409 user_id_in_use` en caso de colision), `emailConfirmed` (crea el usuario ya verificado, omitiendo el correo de verificacion), `companyName`, `organizationId`, `phone`, `locale`, y `customAttributes` (un mapa de cadenas persistido en el usuario y reenviado a los destinos de aprovisionamiento).

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

`userId` es requerido; todos los demas campos son opcionales -- solo los campos proporcionados se actualizan. Cambiar `organizationId` desencadena:
- Rotacion del SecurityStamp (invalida todas las sesiones por cookie dentro de 30 minutos)
- Revocacion de todos los tokens de actualizacion

### Eliminar usuario

```
DELETE /api/v1/profile/{userId}
```

Elimina al usuario, revoca todos los otorgamientos y desaprovisiona de todas las aplicaciones posteriores (mejor esfuerzo).

### Confirmar correo electronico

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar correo de verificacion

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

## Gestion de MFA

### Obtener estado de MFA

```
GET /api/v1/profile/{userId}/mfa
```

Devuelve el estado de MFA y los metodos inscritos de un usuario.

### Restablecer todo MFA

```
DELETE /api/v1/profile/{userId}/mfa
```

Elimina todas las credenciales MFA y establece `MfaEnabled=false`. El usuario debera volver a inscribirse si es requerido.

### Eliminar credencial MFA especifica

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Elimina una credencial MFA especifica (por ejemplo, un autenticador perdido). Si se elimina el ultimo metodo primario, MFA se desactiva.

## Proveedores SSO

### Proveedores SAML

```
POST   /api/v1/saml/connections                    # Crear
GET    /api/v1/saml/connections/{connectionId}     # Obtener uno
PUT    /api/v1/saml/connections/{connectionId}     # Actualizar (parcial -- solo cambian los campos proporcionados)
DELETE /api/v1/saml/connections/{connectionId}     # Eliminar
```

La creacion requiere `connectionName`, `entityId`, y **exactamente uno de** `metadataLocation` (una URL de metadatos) o `metadataXml` (metadatos del IdP pegados, para IdPs sin una URL de metadatos -- se validan al analizarse y se condensan al guardar). Opcional: `nameIdFormat` (omitalo para el valor predeterminado emailAddress, `"none"` para omitir NameIDPolicy -- recomendado para ADFS, o una URN de formato NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Cada conexion obtiene un par de claves SP generado por el servidor; nunca lo devuelve la API. Ver [SAML](saml) para mas detalles.

### Proveedores OIDC

```
POST   /api/v1/oidc/connections                    # Crear
GET    /api/v1/oidc/connections/{connectionId}     # Obtener uno
DELETE /api/v1/oidc/connections/{connectionId}     # Eliminar
```

La creacion requiere `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Opcional: `iconUrl`, `allowedDomains`, `passthroughParams`. El secreto del cliente se protege en reposo y nunca se devuelve. Ver [Federacion OIDC](oidc-federation).

### Dominios SSO

```
GET    /api/v1/sso/domains                 # Listar todos
```

## Clientes

Gestione los clientes OAuth en tiempo de ejecucion. Todas las rutas requieren la politica `IdentityAdmin` (el scope de administracion).

```
GET    /api/v1/clients              # Listar todos los clientes
GET    /api/v1/clients/{clientId}   # Obtener un cliente
POST   /api/v1/clients              # Crear un cliente
PUT    /api/v1/clients/{clientId}   # Actualizar un cliente
DELETE /api/v1/clients/{clientId}   # Eliminar un cliente
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

`POST` devuelve `409` si el cliente ya existe. `PUT` actualiza un cliente existente (`404` si no se encuentra); en una actualizacion, solo los scopes recien anadidos se comprueban contra escalada de privilegios.

Notas:

- **Los hashes de secretos nunca se devuelven.** `clientSecretHashes` se elimina de cada respuesta (listar, obtener, crear, actualizar). En una actualizacion, omitir `clientSecretHashes` conserva el secreto almacenado; proporcionar nuevos hashes lo rota.
- **El scope de administracion no puede otorgarse a un cliente.** Solicitar `AdminApi:Scope` (predeterminado `authagonal-admin`) en `allowedScopes` devuelve `403 forbidden_scope`: ningun cliente puede poseer el scope de administracion, de lo contrario un cliente `client_credentials` podria emitir tokens de administracion indefinidamente.
- Anadir scopes que el llamador no esta autorizado a otorgar devuelve `403`.

## Scopes

Gestione scopes OAuth personalizados en tiempo de ejecucion. Ver [Scopes de OAuth](scopes) para el modelo completo de scopes.

```
GET    /api/v1/scopes           # Listar todos los scopes
GET    /api/v1/scopes/{name}    # Obtener un scope
POST   /api/v1/scopes           # Crear un scope
PUT    /api/v1/scopes/{name}    # Actualizar un scope (solo cambian los campos proporcionados)
DELETE /api/v1/scopes/{name}    # Eliminar un scope
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

Gestione los destinos de aprovisionamiento posteriores en tiempo de ejecucion. Todas las rutas requieren la politica `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # Listar apps (tambien devuelve el limite configurado)
POST   /api/v1/provisioning/apps               # Crear una app
PUT    /api/v1/provisioning/apps/{appId}       # Actualizar una app
DELETE /api/v1/provisioning/apps/{appId}       # Eliminar una app
POST   /api/v1/provisioning/apps/{appId}/test  # Enviar una llamada /try de prueba al callback de la app
```

### Crear / actualizar aplicacion de aprovisionamiento

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
- **La clave API nunca se devuelve.** Las respuestas exponen `hasApiKey` (un booleano) en lugar de la clave en si. En una actualizacion, omitir `apiKey` la deja sin cambios, una cadena vacia la borra, y un valor la reemplaza.
- La creacion esta sujeta a una cuota configurable por despliegue (`IProvisioningAppQuota`); excederla devuelve `400 provisioning_app_limit`. La respuesta de listado incluye el `limit` actual.

### Probar una aplicacion de aprovisionamiento

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envia un `POST {callbackUrl}/try` sintetico con una carga util de ejemplo (y la clave API de la app como token bearer si esta establecida) y devuelve `{ success, statusCode, body }` para que pueda verificar la conectividad desde la interfaz de administracion.

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

La asignacion es por **nombre de rol**, no por id de rol. Devuelve la lista de roles actualizada del usuario.

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

`description` y `expiresInDays` son opcionales (omita `expiresInDays` para un token que no expira). Devuelve el token en texto plano una sola vez. Almacenelo de forma segura -- no se puede recuperar de nuevo.

### Listar tokens

```
GET /api/v1/scim/tokens?clientId=client-id
```

Devuelve los metadatos del token (ID, fecha de creacion) sin el valor del token en texto plano.

### Revocar token

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens

### Suplantar usuario

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emite tokens (de acceso, de actualizacion y —cuando se solicita `openid`— token de identidad) en nombre de un usuario sin requerir sus credenciales. Util para pruebas y soporte. Los parametros se pasan como cadenas de consulta.

| Parametro de consulta | Requerido | Descripcion |
|---|---|---|
| `clientId` | Si | El cliente para el que se emiten los tokens. Los tiempos de vida de los tokens provienen de la configuracion de este cliente. |
| `userId` | Si | El usuario a suplantar. |
| `scopes` | No | Lista de scopes **separados por espacios** (codifique los espacios en la URL). Por defecto, los `AllowedScopes` del cliente cuando se omite. |

Restricciones:

- Los scopes estan limitados a los `AllowedScopes` del cliente: solicitar cualquier scope que el propio cliente no podria solicitar devuelve `400 invalid_scope`.
- El scope de administracion (`AdminApi:Scope`, predeterminado `authagonal-admin`) **no** puede emitirse a traves de este endpoint; solicitarlo devuelve `403 forbidden_scope`. Esto evita que un token de administracion (posiblemente de tiempo limitado) emita un token de acceso/actualizacion de administracion de larga duracion.

La respuesta es una respuesta de token estandar con `access_token`, `refresh_token`, opcionalmente `id_token`, `expires_in` y el `scope` otorgado (separado por espacios).
