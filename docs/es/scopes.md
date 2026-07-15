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
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
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
| `Required` | Si es `true`, el usuario no puede deseleccionar este scope al dar su consentimiento |
| `ShowInDiscoveryDocument` | Si es `true`, el scope aparece en `/.well-known/openid-configuration` bajo `scopes_supported` |
| `UserClaims` | Claims añadidos al token de acceso cuando se concede este scope |

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
