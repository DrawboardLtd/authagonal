---
layout: default
title: Aprovisionamiento SCIM 2.0
locale: es
---

# Aprovisionamiento SCIM 2.0

Authagonal es compatible con SCIM 2.0 (System for Cross-domain Identity Management) para el aprovisionamiento automatizado de usuarios desde proveedores de identidad empresariales como Microsoft Entra ID, Okta y OneLogin.

## Vision general

SCIM es un protocolo de aprovisionamiento entrante: su proveedor de identidad envia los cambios de usuarios y grupos a Authagonal. Es complementario al aprovisionamiento saliente TCC (Try-Confirm-Cancel) existente, que envia usuarios a las aplicaciones posteriores.

**Operaciones admitidas:**
- CRUD de usuarios (crear, leer, actualizar, eliminar mediante desactivacion suave)
- CRUD de grupos con gestion de miembros
- Filtrado (operadores `eq` y `co` sobre `userName`, `externalId`, `displayName`)
- Paginacion: basada en cursor para los listados de usuarios (`cursor`/`nextCursor`), `startIndex` y `count` para grupos
- PATCH para actualizaciones parciales (incluida la desactivacion con `active=false`)
- Asignacion de grupos a roles resuelta en el momento de emision del token

**No admitido:** operaciones masivas, ordenamiento, ETags, gestion de contrasenas via SCIM.

Todos los recursos estan acotados al cliente SCIM que los aprovisiono: un usuario o grupo creado por el cliente de un token SCIM es invisible (404) para cualquier otro cliente SCIM.

## Generar un token SCIM

Los endpoints SCIM se autentican con tokens Bearer estaticos. Genere tokens a traves de la API de administracion:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

La respuesta incluye el token en bruto **una sola vez**. Se almacena como un hash SHA-256 y no puede recuperarse despues, asi que guardelo de forma segura:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Omita `expiresInDays` (o pase `0`) para un token sin expiracion.

### Listar tokens

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Revocar un token

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurar su proveedor de identidad

### URL del tenant

```
https://your-authagonal-instance/scim/v2
```

### Autenticacion

Use **OAuth Bearer Token** con el token generado anteriormente.

### Microsoft Entra ID

1. En el portal de Azure, vaya a **Enterprise Applications** > su aplicacion > **Provisioning**
2. Establezca el modo de aprovisionamiento en **Automatic**
3. Introduzca la Tenant URL: `https://your-instance/scim/v2`
4. Introduzca el Secret Token: el token en bruto del paso de generacion
5. Haga clic en **Test Connection** para verificar
6. Configure las asignaciones de atributos (ver mas abajo)

### Okta

1. En la consola de administracion de Okta, vaya a **Applications** > su aplicacion > **Provisioning**
2. Habilite el **SCIM connector**
3. Establezca la Base URL: `https://your-instance/scim/v2`
4. Establezca el modo de autenticacion: **HTTP Header**
5. Introduzca el token Bearer

### OneLogin

1. En la administracion de OneLogin, vaya a **Applications** > su aplicacion > **Provisioning**
2. Habilite el aprovisionamiento
3. Establezca la SCIM Base URL: `https://your-instance/scim/v2`
4. Establezca el SCIM Bearer Token

## Endpoints SCIM

| Metodo | Ruta | Descripcion |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Listar/filtrar usuarios |
| GET | `/scim/v2/Users/{id}` | Obtener un usuario |
| POST | `/scim/v2/Users` | Crear un usuario |
| PUT | `/scim/v2/Users/{id}` | Reemplazar un usuario |
| PATCH | `/scim/v2/Users/{id}` | Actualizacion parcial |
| DELETE | `/scim/v2/Users/{id}` | Desactivacion suave |
| GET | `/scim/v2/Groups` | Listar/filtrar grupos |
| GET | `/scim/v2/Groups/{id}` | Obtener un grupo |
| POST | `/scim/v2/Groups` | Crear un grupo |
| PUT | `/scim/v2/Groups/{id}` | Reemplazar un grupo |
| PATCH | `/scim/v2/Groups/{id}` | Agregar/eliminar miembros |
| DELETE | `/scim/v2/Groups/{id}` | Eliminar un grupo |
| GET | `/scim/v2/ServiceProviderConfig` | Capacidades |
| GET | `/scim/v2/Schemas` | Definiciones de esquema |
| GET | `/scim/v2/ResourceTypes` | Tipos de recursos |

Cada endpoint tambien esta mapeado sin el segmento `/v2` (por ejemplo, `/scim/Users`) para proveedores de identidad que anexan su propia ruta. Los endpoints de descubrimiento (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, y las URLs base `/scim/` y `/scim/v2/` a secas, que devuelven el ServiceProviderConfig) son anonimos; todo lo demas requiere un token Bearer SCIM.

Los endpoints de usuarios tienen un limite de 200 solicitudes por minuto por cliente SCIM; las solicitudes excedentes reciben un error SCIM con estado `429`.

## Asignacion de atributos

### Atributos de usuario

| Atributo SCIM | Campo de Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (con respaldo en `locale`) | `Locale` |

### Atributos de grupo

| Atributo SCIM | Campo de Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Detalles de comportamiento

### Creacion de usuarios
- Los usuarios aprovisionados via SCIM se crean con `EmailConfirmed = true` (solo SSO, sin contrasena).
- El campo `ScimProvisionedByClientId` registra que cliente SCIM creo al usuario.
- Si el cliente tiene `ProvisioningApps` configurado, el aprovisionamiento TCC se dispara automaticamente. Si el aprovisionamiento rechaza al usuario, la creacion SCIM se revierte con una respuesta `422`.
- Crear un usuario cuyo `userName` o `externalId` ya existe devuelve un conflicto SCIM `409`. Los cambios de correo electronico via PUT o PATCH se verifican contra conflictos de la misma manera.

### Desactivacion de usuarios
- `DELETE /scim/v2/Users/{id}` realiza una **eliminacion suave** estableciendo `IsActive = false`. El registro del usuario se conserva: un `GET /scim/v2/Users/{id}` posterior aun lo devuelve (con `active: false`) en lugar de un 404.
- `PATCH` con `active = false` tambien desactiva al usuario.
- Los usuarios desactivados no pueden iniciar sesion mediante contrasena, SAML u OIDC.
- Todos los grants (tokens de actualizacion, sesiones) se revocan al desactivar.
- El desaprovisionamiento de las aplicaciones posteriores se dispara solo con `DELETE`; una desactivacion via `PATCH` revoca los grants pero deja las aplicaciones posteriores intactas.

### Filtrado
Expresiones de filtro admitidas:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Solo se admiten filtros de un unico atributo. Las expresiones booleanas complejas (`and`, `or`) no se admiten.

Los filtros `eq` sobre `userName` y `externalId` (las consultas que Entra y Okta emiten antes de cada creacion o actualizacion) se resuelven mediante busquedas puntuales indexadas en lugar de un recorrido del listado, por lo que se mantienen rapidos con cualquier cantidad de usuarios. Los demas filtros (`co`, o filtros sobre `displayName`) se aplican mientras se pagina a traves de los usuarios del cliente.

### Paginacion
Los listados de usuarios usan **paginacion por cursor**. Cada pagina de `GET /scim/v2/Users` devuelve una propiedad `nextCursor` en la respuesta de listado; pasela de vuelta como `?cursor=` para obtener la siguiente pagina. Cuando `nextCursor` esta ausente, el listado esta completo. El tamano de pagina se controla con `count` (predeterminado 100, maximo 200).

Solicitar `startIndex` mayor que 1 en el endpoint de Users devuelve un error `400` que le dirige a la paginacion por cursor; no se ofrece paginacion por desplazamiento mas alla de la primera pagina. `totalResults` informa el numero de recursos devueltos en la respuesta (es el total verdadero solo cuando `nextCursor` esta ausente).

Los listados de grupos siguen usando paginacion por desplazamiento con `startIndex`/`count`.

### Membresia de grupos via PATCH
`PATCH /scim/v2/Groups/{id}` acepta las formas de membresia que los principales proveedores de identidad realmente envian:

- **Agregar miembros:** `op: "add"` con `path: "members"` y un arreglo de valores de objetos `{ "value": "user-id" }`. Los duplicados se ignoran.
- **Reemplazar miembros:** `op: "replace"` con `path: "members"` reemplaza toda la membresia con el arreglo suministrado.
- **Eliminar un miembro especifico (arreglo de valores):** `op: "remove"` con `path: "members"` y un arreglo de valores con los ids de los miembros a eliminar (la forma que envia Entra ID).
- **Eliminar un miembro especifico (filtro en la ruta):** `op: "remove"` con `path: 'members[value eq "user-id"]'`, el id transportado en el filtro de la ruta sin valor (la forma que envia Okta para el desaprovisionamiento).
- **Eliminar todos los miembros:** `op: "remove"` con `path: "members"` y sin valor vacia el grupo.

### Asignacion de grupos a roles
La membresia en un grupo SCIM puede otorgar roles de aplicacion. Las asignaciones son una fila por cada par (grupo, rol), y un grupo puede otorgar varios roles. Se resuelven en el momento de **emision del token**: los roles efectivos de un usuario son sus roles asignados directamente mas los roles de cada grupo asignado al que pertenece, de modo que agregar o quitar un miembro de un grupo surte efecto en el siguiente token sin tocar el registro del usuario. Un almacen de asignaciones vacio es un no-op.

Las asignaciones se persisten a traves de `IScimGroupRoleMappingStore` (implementado por los proveedores de almacenamiento de Azure y AWS; en caso contrario se registra un valor predeterminado en memoria) y se gestionan desde la superficie de administracion de la aplicacion anfitriona, no a traves de la propia API SCIM.

Opcionalmente, un cliente con `IncludeGroupsInTokens` habilitado tambien recibe los nombres para mostrar de los grupos SCIM del usuario como un claim `groups` en los tokens emitidos.

## Limitaciones conocidas

- **Sin operaciones masivas:** los usuarios y grupos deben aprovisionarse individualmente.
- **Sin ordenamiento:** los listados de usuarios devuelven el orden de almacenamiento bajo paginacion por cursor; los listados de grupos se ordenan por fecha de creacion.
- **Subconjunto de filtros:** solo los operadores `eq` y `co` sobre `userName`, `externalId` y `displayName` (grupos: `displayName` y `externalId`).
- **Sin gestion de contrasenas:** los usuarios aprovisionados via SCIM se autentican solo mediante SSO.
- **Solo eliminacion suave:** `DELETE` desactiva en lugar de eliminar permanentemente a los usuarios.
