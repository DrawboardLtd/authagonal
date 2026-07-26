---
layout: default
title: Scopes de OAuth
locale: es
---

# Scopes de OAuth

Authagonal admite tanto scopes **integrados** de OAuth/OIDC como scopes **personalizados** gestionados en tiempo de ejecución. Los scopes personalizados se persisten, se anuncian mediante el documento de descubrimiento y se muestran en la pantalla de consentimiento junto a los integrados.

## Scopes integrados

Estos scopes siempre están disponibles y no necesitan registrarse:

| Scope | Propósito |
|---|---|
| `openid` | Requerido para iniciar un flujo OIDC. Emite un token de identidad. |
| `profile` | Claims de perfil estándar (name, family_name, given_name, etc.) |
| `email` | Dirección de correo electrónico y claims `email_verified` |
| `offline_access` | Emite un token de actualización junto con el token de acceso |

## Scopes personalizados

Los scopes personalizados se gestionan a través de la API de administración en `/api/v1/scopes`. Requieren un token de acceso JWT con el scope `authagonal-admin` (configurable mediante `AdminApi:Scope`).

### Modelo de scope

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Campo | Descripción |
|---|---|
| `Name` | El identificador del scope enviado en las solicitudes de token (por ejemplo, `billing.read`) |
| `DisplayName` | Nombre legible para humanos que se muestra en la pantalla de consentimiento |
| `Description` | Descripción más larga que se muestra en la pantalla de consentimiento |
| `Emphasize` | Si es `true`, la pantalla de consentimiento resalta este scope como sensible |
| `Group` | Encabezado bajo el que se agrupa este scope en la pantalla de consentimiento. Solo presentación — nunca afecta a lo que se concede |
| `Required` | Si es `true`, el usuario no puede deseleccionar este scope al dar su consentimiento |
| `ShowInDiscoveryDocument` | Si es `true`, el scope aparece en `/.well-known/openid-configuration` bajo `scopes_supported` |
| `AllowedRoles` | Roles que el usuario debe tener para que se le conceda este scope. Vacío (por defecto) lo deja sin restricción — véase [Scopes restringidos por rol](#scopes-restringidos-por-rol) |
| `UserClaims` | Claims añadidos al token de acceso cuando se concede este scope |

### Scopes restringidos por rol

El `AllowedScopes` de un cliente responde a *si esta aplicación puede solicitar este scope* — una
pregunta que se resuelve antes de que nadie haya iniciado sesión. `AllowedRoles` responde a la otra
mitad: *si esta persona puede tenerlo*. Ambas restricciones se aplican, y ninguna sustituye a la otra.

```json
{
  "name": "staff-admin",
  "displayName": "Administración interna",
  "allowedRoles": ["staff", "super-admin"]
}
```

A un usuario que no tenga ninguno de los roles indicados se le **elimina el scope de la concesión**,
no se le rechaza: el cliente pidió su conjunto completo y se le informa, mediante el `scope`
devuelto en la respuesta del token (RFC 6749 §3.3), de que ha obtenido menos. Esto es lo que permite
que una misma aplicación sirva tanto al personal interno como a todos los demás — la superficie
interna es un scope más entre varios, y solo lo reciben las personas autorizadas.

Una solicitud en la que se eliminan *todos* los scopes solicitados falla con `access_denied`, porque
no queda nada para lo que emitir un token.

La restricción se aplica en todos los flujos que emiten un token para una persona:

| Flujo | Dónde se aplica |
|---|---|
| Authorization code | En `/connect/authorize`, una vez conocido el usuario y **antes** del consentimiento — así la pantalla nunca ofrece un permiso que no puede concederse |
| Device code | En `/api/auth/device/approve`, el primer punto de ese flujo en el que se conoce al sujeto |
| Refresh | En cada rotación, contra los roles resueltos de nuevo. Aquí es donde revocar un rol surte efecto realmente, ya que la concesión sigue registrando lo aprobado al iniciar sesión |
| Token exchange | No se restringe por separado: un intercambio solo puede reducir el alcance dentro de los scopes del propio token de sujeto, por lo que nunca puede alcanzar uno que el sujeto no tuviera concedido |

Las concesiones de tipo client-credentials no tienen sujeto y quedan deliberadamente al margen — la
autoridad de un cliente máquina es su registro.

Sembrar un scope desde la configuración puede añadir o cambiar `AllowedRoles`, pero no puede
vaciarlo (igual que con `UserClaims`, un campo omitido conserva el valor almacenado). Para eliminar
la restricción, haz `PUT` del scope con un array vacío explícito.

## Endpoints de administración

### Listar scopes

```
GET /api/v1/scopes
```

Devuelve `{ "scopes": [ ... ] }`.

### Obtener scope

```
GET /api/v1/scopes/{name}
```

Devuelve el scope o `404` si no se encuentra.

### Crear scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Devuelve `201 Created` con el scope. Devuelve `409` si ya existe un scope con el mismo nombre.

### Actualizar scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Solo se actualizan los campos proporcionados; los campos omitidos conservan sus valores actuales.

### Eliminar scope

```
DELETE /api/v1/scopes/{name}
```

Devuelve `204 No Content` (`404` si el scope no existe). Los tokens ya emitidos que incluyen este scope siguen siendo válidos hasta que expiran; revóquelos explícitamente mediante `/connect/revocation` si es necesario.

## Documento de descubrimiento

Los scopes con `ShowInDiscoveryDocument = true` aparecen bajo `scopes_supported` en `/.well-known/openid-configuration`. Los scopes integrados siempre se anuncian.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Pantalla de consentimiento

Cuando un cliente solicita un scope que no está en su lista de omisión de consentimiento, la página de consentimiento enumera cada scope solicitado por `DisplayName` (recurriendo a `Name`) con la `Description` debajo. Los scopes con `Emphasize = true` reciben un tratamiento visual distintivo. Los scopes `Required` no pueden deseleccionarse.

Consulte [Pantalla de consentimiento de OAuth](index#features) para el flujo de cara al usuario.

## Registro dinámico de clientes

Los clientes registrados mediante el [registro dinámico de clientes](client-registration) solo pueden solicitar scopes que sean integrados o que se hayan creado previamente mediante la API de administración. Los scopes desconocidos se rechazan con `invalid_scope`.
