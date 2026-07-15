---
layout: default
title: Aprovisionamiento SCIM 2.0
locale: es
---

# Aprovisionamiento SCIM 2.0

Authagonal es compatible con SCIM 2.0 (System for Cross-domain Identity Management) para el aprovisionamiento automatizado de usuarios desde proveedores de identidad empresariales como Microsoft Entra ID, Okta y OneLogin.

## Visión general

SCIM es un protocolo de aprovisionamiento entrante: su proveedor de identidad envía los cambios de usuarios y grupos a Authagonal. Es complementario al aprovisionamiento saliente TCC (Try-Confirm-Cancel) existente, que envía usuarios a las aplicaciones posteriores.

**Operaciones admitidas:**
- CRUD de usuarios (crear, leer, actualizar, eliminar mediante desactivación suave)
- CRUD de grupos con gestión de miembros
- Filtrado (operadores `eq` y `co` sobre `userName`, `externalId`, `displayName`)
- Paginación: basada en cursor para los listados de usuarios (`cursor`/`nextCursor`), `startIndex` y `count` para grupos
- PATCH para actualizaciones parciales (incluida la desactivación con `active=false`)
- Asignación de grupos a roles resuelta en el momento de emisión del token

**No admitido:** operaciones masivas, ordenamiento, ETags, gestión de contraseñas vía SCIM.

Todos los recursos están acotados al cliente SCIM que los aprovisionó: un usuario o grupo creado por el cliente de un token SCIM es invisible (404) para cualquier otro cliente SCIM.

## Generar un token SCIM

Los endpoints SCIM se autentican con tokens Bearer estáticos. Genere tokens a través de la API de administración:

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

La respuesta incluye el token en bruto **una sola vez**. Se almacena como un hash SHA-256 y no puede recuperarse después, así que guárdelo de forma segura:

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

Omita `expiresInDays` (o pase `0`) para un token sin expiración.

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

### Autenticación

Use **OAuth Bearer Token** con el token generado anteriormente.

### Microsoft Entra ID

1. En el portal de Azure, vaya a **Enterprise Applications** > su aplicación > **Provisioning**
2. Establezca el modo de aprovisionamiento en **Automatic**
3. Introduzca la Tenant URL: `https://your-instance/scim/v2`
4. Introduzca el Secret Token: el token en bruto del paso de generación
5. Haga clic en **Test Connection** para verificar
6. Configure las asignaciones de atributos (ver más abajo)

### Okta

1. En la consola de administración de Okta, vaya a **Applications** > su aplicación > **Provisioning**
2. Habilite el **SCIM connector**
3. Establezca la Base URL: `https://your-instance/scim/v2`
4. Establezca el modo de autenticación: **HTTP Header**
5. Introduzca el token Bearer

### OneLogin

1. En la administración de OneLogin, vaya a **Applications** > su aplicación > **Provisioning**
2. Habilite el aprovisionamiento
3. Establezca la SCIM Base URL: `https://your-instance/scim/v2`
4. Establezca el SCIM Bearer Token

## Endpoints SCIM

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Listar/filtrar usuarios |
| GET | `/scim/v2/Users/{id}` | Obtener un usuario |
| POST | `/scim/v2/Users` | Crear un usuario |
| PUT | `/scim/v2/Users/{id}` | Reemplazar un usuario |
| PATCH | `/scim/v2/Users/{id}` | Actualización parcial |
| DELETE | `/scim/v2/Users/{id}` | Desactivación suave |
| GET | `/scim/v2/Groups` | Listar/filtrar grupos |
| GET | `/scim/v2/Groups/{id}` | Obtener un grupo |
| POST | `/scim/v2/Groups` | Crear un grupo |
| PUT | `/scim/v2/Groups/{id}` | Reemplazar un grupo |
| PATCH | `/scim/v2/Groups/{id}` | Agregar/eliminar miembros |
| DELETE | `/scim/v2/Groups/{id}` | Eliminar un grupo |
| GET | `/scim/v2/ServiceProviderConfig` | Capacidades |
| GET | `/scim/v2/Schemas` | Definiciones de esquema |
| GET | `/scim/v2/ResourceTypes` | Tipos de recursos |

Cada endpoint también está mapeado sin el segmento `/v2` (por ejemplo, `/scim/Users`) para proveedores de identidad que anexan su propia ruta. Los endpoints de descubrimiento (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, y las URLs base `/scim/` y `/scim/v2/` a secas, que devuelven el ServiceProviderConfig) son anónimos; todo lo demás requiere un token Bearer SCIM.

Los endpoints de usuarios tienen un límite de 200 solicitudes por minuto por cliente SCIM; las solicitudes excedentes reciben un error SCIM con estado `429`.

## Asignación de atributos

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

### Creación de usuarios
- Los usuarios aprovisionados vía SCIM se crean con `EmailConfirmed = true` (solo SSO, sin contraseña).
- El campo `ScimProvisionedByClientId` registra qué cliente SCIM creó al usuario.
- Si el cliente tiene `ProvisioningApps` configurado, el aprovisionamiento TCC se dispara automáticamente. Si el aprovisionamiento rechaza al usuario, la creación SCIM se revierte con una respuesta `422`.
- Crear un usuario cuyo `userName` o `externalId` ya existe devuelve un conflicto SCIM `409`. Los cambios de correo electrónico vía PUT o PATCH se verifican contra conflictos de la misma manera.

### Desactivación de usuarios
- `DELETE /scim/v2/Users/{id}` realiza una **eliminación suave** estableciendo `IsActive = false`. El registro del usuario se conserva: un `GET /scim/v2/Users/{id}` posterior aún lo devuelve (con `active: false`) en lugar de un 404.
- `PATCH` con `active = false` también desactiva al usuario.
- Los usuarios desactivados no pueden iniciar sesión mediante contraseña, SAML u OIDC.
- Todos los grants (tokens de actualización, sesiones) se revocan al desactivar.
- El desaprovisionamiento de las aplicaciones posteriores se dispara solo con `DELETE`; una desactivación vía `PATCH` revoca los grants pero deja las aplicaciones posteriores intactas.

### Filtrado
Expresiones de filtro admitidas:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Solo se admiten filtros de un único atributo. Las expresiones booleanas complejas (`and`, `or`) no se admiten.

Los filtros `eq` sobre `userName` y `externalId` (las consultas que Entra y Okta emiten antes de cada creación o actualización) se resuelven mediante búsquedas puntuales indexadas en lugar de un recorrido del listado, por lo que se mantienen rápidos con cualquier cantidad de usuarios. Los demás filtros (`co`, o filtros sobre `displayName`) se aplican mientras se pagina a través de los usuarios del cliente.

### Paginación
Los listados de usuarios usan **paginación por cursor**. Cada página de `GET /scim/v2/Users` devuelve una propiedad `nextCursor` en la respuesta de listado; pásela de vuelta como `?cursor=` para obtener la siguiente página. Cuando `nextCursor` está ausente, el listado está completo. El tamaño de página se controla con `count` (predeterminado 100, máximo 200).

Solicitar `startIndex` mayor que 1 en el endpoint de Users devuelve un error `400` que le dirige a la paginación por cursor; no se ofrece paginación por desplazamiento más allá de la primera página. `totalResults` informa el número de recursos devueltos en la respuesta (es el total verdadero solo cuando `nextCursor` está ausente).

Los listados de grupos siguen usando paginación por desplazamiento con `startIndex`/`count`.

### Membresía de grupos vía PATCH
`PATCH /scim/v2/Groups/{id}` acepta las formas de membresía que los principales proveedores de identidad realmente envían:

- **Agregar miembros:** `op: "add"` con `path: "members"` y un arreglo de valores de objetos `{ "value": "user-id" }`. Los duplicados se ignoran.
- **Reemplazar miembros:** `op: "replace"` con `path: "members"` reemplaza toda la membresía con el arreglo suministrado.
- **Eliminar un miembro específico (arreglo de valores):** `op: "remove"` con `path: "members"` y un arreglo de valores con los ids de los miembros a eliminar (la forma que envía Entra ID).
- **Eliminar un miembro específico (filtro en la ruta):** `op: "remove"` con `path: 'members[value eq "user-id"]'`, el id transportado en el filtro de la ruta sin valor (la forma que envía Okta para el desaprovisionamiento).
- **Eliminar todos los miembros:** `op: "remove"` con `path: "members"` y sin valor vacía el grupo.

### Asignación de grupos a roles
La membresía en un grupo SCIM puede otorgar roles de aplicación. Las asignaciones son una fila por cada par (grupo, rol), y un grupo puede otorgar varios roles. Se resuelven en el momento de **emisión del token**: los roles efectivos de un usuario son sus roles asignados directamente más los roles de cada grupo asignado al que pertenece, de modo que agregar o quitar un miembro de un grupo surte efecto en el siguiente token sin tocar el registro del usuario. Un almacén de asignaciones vacío es un no-op.

Las asignaciones se persisten a través de `IScimGroupRoleMappingStore` (implementado por los proveedores de almacenamiento de Azure y AWS; en caso contrario se registra un valor predeterminado en memoria) y se gestionan desde la superficie de administración de la aplicación anfitriona, no a través de la propia API SCIM.

Opcionalmente, un cliente con `IncludeGroupsInTokens` habilitado también recibe los nombres para mostrar de los grupos SCIM del usuario como un claim `groups` en los tokens emitidos.

## Limitaciones conocidas

- **Sin operaciones masivas:** los usuarios y grupos deben aprovisionarse individualmente.
- **Sin ordenamiento:** los listados de usuarios devuelven el orden de almacenamiento bajo paginación por cursor; los listados de grupos se ordenan por fecha de creación.
- **Subconjunto de filtros:** solo los operadores `eq` y `co` sobre `userName`, `externalId` y `displayName` (grupos: `displayName` y `externalId`).
- **Sin gestión de contraseñas:** los usuarios aprovisionados vía SCIM se autentican solo mediante SSO.
- **Solo eliminación suave:** `DELETE` desactiva en lugar de eliminar permanentemente a los usuarios.
