---
layout: default
title: Aprovisionamiento
locale: es
---

# Aprovisionamiento TCC

Authagonal aprovisiona usuarios en aplicaciones posteriores utilizando el patron **Try-Confirm-Cancel (TCC)**. Esto garantiza que todas las aplicaciones esten de acuerdo antes de que un usuario obtenga acceso, con una reversion limpia si alguna aplicacion rechaza.

## Cuando se ejecuta el aprovisionamiento

El aprovisionamiento se ejecuta automaticamente cada vez que se crea un usuario, independientemente de la ruta de creacion:

| Endpoint | Disparador |
|---|---|
| `POST /api/v1/profile/` | Creacion de usuario por administrador |
| `POST /api/auth/register` | Registro de autoservicio |
| SAML ACS (`POST /saml/{id}/acs`) | Primer inicio de sesion SSO (usuario nuevo) |
| OIDC callback (`GET /oidc/callback`) | Primer inicio de sesion SSO (usuario nuevo) |
| SCIM (`POST /scim/v2/Users`) | Aprovisionamiento del proveedor de identidad |
| `GET /connect/authorize` | Primera autorizacion a traves de un cliente con `ProvisioningApps` |

Las combinaciones aplicacion/usuario ya aprovisionadas se omiten (rastreadas en la tabla `UserProvisions`).

Las rutas de creacion de usuarios aprovisionan en **todas las aplicaciones configuradas**. El endpoint de autorizacion aprovisiona unicamente en la lista `ProvisioningApps` del cliente.

**En caso de rechazo:** Si alguna aplicacion de aprovisionamiento rechaza al usuario en la fase Try, el usuario recien creado se elimina. Esto evita usuarios creados a medias. Las rutas de creacion por API (administrador, registro, SCIM) devuelven `422 Unprocessable Entity` con el motivo del rechazo; los callbacks SSO de SAML/OIDC devuelven `400 Bad Request`; el endpoint de autorizacion redirige de vuelta al cliente con `error=access_denied`.

## Configuracion

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

`TryTimeoutSeconds` es opcional (predeterminado 60). Aumentelo cuando la aplicacion posterior realice trabajo real durante Try. Confirm y Cancel siempre usan un tiempo de espera fijo y corto (10 segundos) y no son ajustables; siempre deberian ser economicos.

### 2. Asignar aplicaciones a clientes

Cada cliente declara en que aplicaciones deben aprovisionarse sus usuarios, mediante el campo `provisioningApps` en el registro del cliente. Configurelo a traves de la API de administracion de clientes (la configuracion de inicializacion `Clients` no incluye este campo):

```
PUT /api/v1/clients/web-app
{
  "clientId": "web-app",
  "provisioningApps": ["my-backend"],
  ...
}
```

Cuando un usuario se autoriza a traves de `web-app`, se aprovisiona en `my-backend` si aun no lo ha sido.

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
| `200` | `{ "approved": true }` | El usuario puede ser aprovisionado. La aplicacion crea un registro **pendiente**. |
| `200` | `{ "approved": false, "reason": "..." }` | El usuario es rechazado. No se crea ningun registro. |
| No-2xx | Cualquiera | Se trata como un fallo. |

El `transactionId` identifica este intento de aprovisionamiento. Su aplicacion debe almacenarlo junto al registro pendiente.

Una respuesta aprobada tambien puede devolver `organizationId` o `customAttributes`. Authagonal los fusiona en el usuario: `organizationId` se aplica solo si el usuario aun no tiene uno (las aplicaciones posteriores de la misma transaccion ven la asignacion anterior), y las entradas de `customAttributes` se fusionan clave por clave. Ambos se propagan a los tokens (claim `org_id`; los atributos personalizados a traves de la configuracion `UserClaims` del scope).

### Fase 2: Confirm

Se llama solo si **todas** las aplicaciones devolvieron `approved: true` en la fase try.

**Solicitud:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `200` (cualquier cuerpo). Su aplicacion promueve el registro pendiente a confirmado.

### Fase 3: Cancel

Se llama si el try de **alguna** aplicacion fue rechazado o fallo, para limpiar las aplicaciones que tuvieron exito en la fase try.

**Solicitud:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `200` (cualquier cuerpo). Su aplicacion elimina el registro pendiente.

La cancelacion se realiza con el mejor esfuerzo: si falla, Authagonal registra el error y continua. Su aplicacion deberia **limpiar los registros no confirmados despues de un TTL** (por ejemplo, 1 hora) como red de seguridad.

## Diagrama de flujo

```
Authorize Endpoint
    |
    +- User authenticated ✓
    +- Client requires apps: [A, B]
    +- User already provisioned into: [A]
    +- Need to provision: [B]
    |
    +- TRY B ------------>App B: create pending record
    |   +- approved: true
    |
    +- CONFIRM B -------->App B: promote to confirmed
    |   +- 200 OK
    |
    +- Store provision record (userId, "B")
    +- Issue authorization code
    +- Redirect to client
```

### En caso de fallo

```
    +- TRY A ------------>App A: create pending record
    |   +- approved: true
    |
    +- TRY B ------------>App B: rejects
    |   +- approved: false, reason: "No license available"
    |
    +- CANCEL A --------->App A: delete pending record
    |
    +- Redirect with error=access_denied
```

### En caso de fallo parcial de confirmacion

Si algunas confirmaciones tienen exito pero una falla, las aplicaciones confirmadas exitosamente tienen sus registros de aprovisionamiento almacenados (por lo que no se reintentaran), y las aplicaciones que aun esperan confirmacion se cancelan. El usuario ve un error y puede reintentar; solo las aplicaciones que no confirmaron se intentaran la proxima vez.

## Resolucion personalizada de aplicaciones

Por defecto, las aplicaciones de aprovisionamiento se leen de la seccion de configuracion `ProvisioningApps` a traves de `ConfigProvisioningAppProvider`. Anule `IProvisioningAppProvider` para resolver aplicaciones dinamicamente, por ejemplo desde una base de datos o por tenant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

El proveedor devuelve una lista de aplicaciones y sus URLs de callback. El `TccProvisioningOrchestrator` llama a Try/Confirm/Cancel en cada una.

Para CRUD en tiempo de ejecucion sin un proveedor personalizado, la biblioteca incluye `StoreProvisioningAppProvider`, respaldado por `IProvisioningAppStore`. Registrelo explicitamente (mismo patron que el anterior) y gestione las aplicaciones a traves de la API de administracion en `/api/v1/provisioning/apps` (list/create/update/delete, mas `POST /{appId}/test` para probar el endpoint Try de una aplicacion).

## Desaprovisionamiento

Cuando un usuario se elimina mediante la API de administracion (`DELETE /api/v1/profile/{userId}`) o se desaprovisiona mediante SCIM (`DELETE /scim/v2/Users/{id}`, una eliminacion suave que desactiva al usuario), Authagonal llama a `DELETE {CallbackUrl}/users/{userId}` en cada aplicacion en la que el usuario fue aprovisionado. Esto se realiza con el mejor esfuerzo: los fallos se registran pero no bloquean la eliminacion.

## Implementacion de los endpoints en origen

### Ejemplo minimo (Node.js/Express)

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
