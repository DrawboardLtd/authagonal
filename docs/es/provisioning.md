---
layout: default
title: Aprovisionamiento
locale: es
---

# Aprovisionamiento TCC

Authagonal aprovisiona usuarios en aplicaciones posteriores utilizando el patrón **Try-Confirm-Cancel (TCC)**. Esto garantiza que todas las aplicaciones estén de acuerdo antes de que un usuario obtenga acceso, con una reversión limpia si alguna aplicación rechaza.

## Cuándo se ejecuta el aprovisionamiento

El aprovisionamiento se ejecuta automáticamente cada vez que se crea un usuario, independientemente de la ruta de creación:

| Endpoint | Disparador |
|---|---|
| `POST /api/v1/profile/` | Creación de usuario por administrador |
| `POST /api/auth/register` | Registro de autoservicio |
| SAML ACS (`POST /saml/{id}/acs`) | Primer inicio de sesión SSO (usuario nuevo) |
| OIDC callback (`GET /oidc/callback`) | Primer inicio de sesión SSO (usuario nuevo) |
| SCIM (`POST /scim/v2/Users`) | Aprovisionamiento del proveedor de identidad |
| `GET /connect/authorize` | Primera autorización a través de un cliente con `ProvisioningApps` |

Las combinaciones aplicación/usuario ya aprovisionadas se omiten (rastreadas en la tabla `UserProvisions`).

Las rutas de creación de usuarios aprovisionan en **todas las aplicaciones configuradas**. El endpoint de autorización aprovisiona únicamente en la lista `ProvisioningApps` del cliente.

**En caso de rechazo:** Si alguna aplicación de aprovisionamiento rechaza al usuario en la fase Try, el usuario recién creado se elimina. Esto evita usuarios creados a medias. Las rutas de creación por API (administrador, registro, SCIM) devuelven `422 Unprocessable Entity` con el motivo del rechazo; los callbacks SSO de SAML/OIDC devuelven `400 Bad Request`; el endpoint de autorización redirige de vuelta al cliente con `error=access_denied`.

## Configuración

### 1. Definir aplicaciones de aprovisionamiento

En `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` es opcional (predeterminado 60). Auméntelo cuando la aplicación posterior realice trabajo real durante Try. Confirm y Cancel siempre usan un tiempo de espera fijo y corto (10 segundos) y no son ajustables; siempre deberían ser económicos.

### 2. Asignar aplicaciones a clientes

Cada cliente declara en qué aplicaciones deben aprovisionarse sus usuarios, mediante el campo `provisioningApps` en el registro del cliente. Configúrelo a través de la API de administración de clientes (la configuración de inicialización `Clients` no incluye este campo):

```
PUT /api/v1/clients/web-app
{
  "clientId": "web-app",
  "provisioningApps": ["my-backend"],
  ...
}
```

Cuando un usuario se autoriza a través de `web-app`, se aprovisiona en `my-backend` si aún no lo ha sido.

## Protocolo TCC

Authagonal realiza tres tipos de llamadas HTTP a su endpoint de aprovisionamiento. Todas usan `POST` con cuerpos JSON y `Authorization: Bearer {ApiKey}`.

### Fase 1: Try

**Solicitud:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Los campos nulos (incluido `customAttributes` cuando el usuario no tiene ninguno) se omiten de la carga.

**Respuestas esperadas:**

| Estado | Cuerpo | Significado |
|---|---|---|
| `200` | `{ "approved": true }` | El usuario puede ser aprovisionado. La aplicación crea un registro **pendiente**. |
| `200` | `{ "approved": false, "reason": "..." }` | El usuario es rechazado. No se crea ningún registro. |
| No-2xx | Cualquiera | Se trata como un fallo. |

El `transactionId` identifica este intento de aprovisionamiento. Su aplicación debe almacenarlo junto al registro pendiente.

Una respuesta aprobada también puede devolver `organizationId` o `customAttributes`. Authagonal los fusiona en el usuario: `organizationId` se aplica solo si el usuario aún no tiene uno (las aplicaciones posteriores de la misma transacción ven la asignación anterior), y las entradas de `customAttributes` se fusionan clave por clave. Ambos se propagan a los tokens (claim `org_id`; los atributos personalizados a través de la configuración `UserClaims` del scope).

### Fase 2: Confirm

Se llama solo si **todas** las aplicaciones devolvieron `approved: true` en la fase try.

**Solicitud:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `200` (cualquier cuerpo). Su aplicación promueve el registro pendiente a confirmado.

### Fase 3: Cancel

Se llama si el try de **alguna** aplicación fue rechazado o falló, para limpiar las aplicaciones que tuvieron éxito en la fase try.

**Solicitud:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `200` (cualquier cuerpo). Su aplicación elimina el registro pendiente.

La cancelación se realiza con el mejor esfuerzo: si falla, Authagonal registra el error y continúa. Su aplicación debería **limpiar los registros no confirmados después de un TTL** (por ejemplo, 1 hora) como red de seguridad.

## Diagrama de flujo

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### En caso de fallo

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### En caso de fallo parcial de confirmación

Si algunas confirmaciones tienen éxito pero una falla, las aplicaciones confirmadas exitosamente tienen sus registros de aprovisionamiento almacenados (por lo que no se reintentarán), y las aplicaciones que aún esperan confirmación se cancelan. El usuario ve un error y puede reintentar; solo las aplicaciones que no confirmaron se intentarán la próxima vez.

## Resolución personalizada de aplicaciones

Por defecto, las aplicaciones de aprovisionamiento se leen de la sección de configuración `ProvisioningApps` a través de `ConfigProvisioningAppProvider`. Anule `IProvisioningAppProvider` para resolver aplicaciones dinámicamente, por ejemplo desde una base de datos o por tenant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

El proveedor devuelve una lista de aplicaciones y sus URLs de callback. El `TccProvisioningOrchestrator` llama a Try/Confirm/Cancel en cada una.

Para CRUD en tiempo de ejecución sin un proveedor personalizado, la biblioteca incluye `StoreProvisioningAppProvider`, respaldado por `IProvisioningAppStore`. Regístrelo explícitamente (mismo patrón que el anterior) y gestione las aplicaciones a través de la API de administración en `/api/v1/provisioning/apps` (list/create/update/delete, más `POST /{appId}/test` para probar el endpoint Try de una aplicación).

## Desaprovisionamiento

Cuando un usuario se elimina mediante la API de administración (`DELETE /api/v1/profile/{userId}`) o se desaprovisiona mediante SCIM (`DELETE /scim/v2/Users/{id}`, una eliminación suave que desactiva al usuario), Authagonal llama a `DELETE {CallbackUrl}/users/{userId}` en cada aplicación en la que el usuario fue aprovisionado. Esto se realiza con el mejor esfuerzo: los fallos se registran pero no bloquean la eliminación.

## Implementación de los endpoints en origen

### Ejemplo mínimo (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
