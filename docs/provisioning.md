---
layout: default
title: Provisioning
---

# TCC Provisioning

Authagonal provisions users into downstream applications using the **Try-Confirm-Cancel (TCC)** pattern. This ensures that all apps agree before a user gains access, with clean rollback if any app rejects.

## When Provisioning Runs

Provisioning runs automatically whenever a user is created, regardless of the creation path:

| Endpoint | Trigger |
|---|---|
| `POST /api/v1/profile/` | Admin user creation |
| `POST /api/auth/register` | Self-service registration |
| SAML ACS (`POST /saml/{id}/acs`) | First SSO login (new user) |
| OIDC callback (`GET /oidc/callback`) | First SSO login (new user) |
| SCIM (`POST /scim/v2/Users`) | Identity provider provisioning |
| `GET /connect/authorize` | First authorization through a client with `ProvisioningApps` |

Already-provisioned app/user combinations are skipped (tracked in the `UserProvisions` table).

**On rejection:** If any provisioning app rejects the user in the Try phase, the user is deleted and the endpoint returns `422 Unprocessable Entity` with the rejection reason. This prevents half-created users.

## Configuration

### 1. Define Provisioning Apps

In `appsettings.json`:

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

### 2. Assign Apps to Clients

Each client declares which apps its users must be provisioned into:

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

When a user authorizes through `web-app`, they are provisioned into `my-backend` if they haven't been already.

## TCC Protocol

Authagonal makes three types of HTTP calls to your provisioning endpoint. All use `POST` with JSON bodies and `Authorization: Bearer {ApiKey}`.

### Phase 1: Try

**Request:** `POST {CallbackUrl}/try`

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

**Expected responses:**

| Status | Body | Meaning |
|---|---|---|
| `200` | `{ "approved": true }` | User can be provisioned. App creates a **pending** record. |
| `200` | `{ "approved": false, "reason": "..." }` | User is rejected. No record created. |
| Non-2xx | Any | Treated as failure. |

The `transactionId` identifies this provisioning attempt. Your app should store it alongside the pending record.

### Phase 2: Confirm

Called only if **all** apps returned `approved: true` in the try phase.

**Request:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Expected response:** `200` (any body). Your app promotes the pending record to confirmed.

### Phase 3: Cancel

Called if **any** app's try was rejected or failed, to clean up the apps that did succeed in the try phase.

**Request:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Expected response:** `200` (any body). Your app deletes the pending record.

Cancel is best-effort — if it fails, Authagonal logs the error and moves on. Your app should **garbage-collect unconfirmed records after a TTL** (e.g., 1 hour) as a safety net.

## Flow Diagram

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

### On Failure

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

### On Partial Confirm Failure

If some confirms succeed but one fails, the successfully confirmed apps have their provision records stored (so they won't be retried). The user sees an error and can retry — only the failed app will be attempted next time.

## Custom App Resolution

By default, provisioning apps are read from the `ProvisioningApps` configuration section via `ConfigProvisioningAppProvider`. Override `IProvisioningAppProvider` to resolve apps dynamically — for example, from a database or per-tenant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

The provider returns a list of apps and their callback URLs. The `TccProvisioningOrchestrator` calls Try/Confirm/Cancel on each.

## Deprovisioning

When a user is deleted via the admin API (`DELETE /api/v1/profile/{userId}`), Authagonal calls `DELETE {CallbackUrl}/users/{userId}` on each app the user was provisioned into. This is best-effort — failures are logged but don't block the deletion.

## Implementing the Upstream Endpoints

### Minimal Example (Node.js/Express)

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
