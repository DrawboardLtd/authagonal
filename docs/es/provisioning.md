---
layout: default
title: Aprovisionamiento
locale: es
---

# Aprovisionamiento TCC

Authagonal aprovisiona usuarios en aplicaciones posteriores utilizando el patron **Try-Confirm-Cancel (TCC)**. Esto garantiza que todas las aplicaciones esten de acuerdo antes de que un usuario obtenga acceso, con una reversion limpia si alguna aplicacion rechaza.

## Cuando se ejecuta el aprovisionamiento

El aprovisionamiento se ejecuta en el **endpoint de autorizacion** (`/connect/authorize`), despues de que el usuario se autentica pero antes de que se emita un codigo de autorizacion. Esto significa:

- Se ejecuta en el primer inicio de sesion del usuario a traves de un cliente que requiere aprovisionamiento
- Las combinaciones aplicacion/usuario ya aprovisionadas se omiten (rastreadas en la tabla `UserProvisions`)
- Si el aprovisionamiento falla, la solicitud de autorizacion devuelve `access_denied` -- no se emite ningun codigo

## Configuracion

### 1. Definir aplicaciones de aprovisionamiento

En `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token"
    }
  }
}
```

### 2. Asignar aplicaciones a clientes

Cada cliente declara en que aplicaciones deben aprovisionarse sus usuarios:

```json
{
  "Clients": [
    {
      "ClientId": "web-app",
      "ProvisioningApps": ["my-backend"],
      ...
    }
  ]
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
  "organizationId": "org-id-or-null"
}
```

**Respuestas esperadas:**

| Estado | Cuerpo | Significado |
|---|---|---|
| `200` | `{ "approved": true }` | El usuario puede ser aprovisionado. La aplicacion crea un registro **pendiente**. |
| `200` | `{ "approved": false, "reason": "..." }` | El usuario es rechazado. No se crea ningun registro. |
| No-2xx | Cualquiera | Se trata como un fallo. |

El `transactionId` identifica este intento de aprovisionamiento. Su aplicacion debe almacenarlo junto al registro pendiente.

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

La cancelacion se realiza con el mejor esfuerzo -- si falla, Authagonal registra el error y continua. Su aplicacion deberia **limpiar los registros no confirmados despues de un TTL** (por ejemplo, 1 hora) como red de seguridad.

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

Si algunas confirmaciones tienen exito pero una falla, las aplicaciones confirmadas exitosamente tienen sus registros de aprovisionamiento almacenados (por lo que no se reintentaran). El usuario ve un error y puede reintentar -- solo la aplicacion fallida se intentara la proxima vez.

## Desaprovisionamiento

Cuando un usuario se elimina mediante la API de administracion (`DELETE /api/v1/profile/{userId}`), Authagonal llama a `DELETE {CallbackUrl}/users/{userId}` en cada aplicacion en la que el usuario fue aprovisionado. Esto se realiza con el mejor esfuerzo -- los fallos se registran pero no bloquean la eliminacion.

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
