---
layout: default
title: Provisioning
---

# TCC Provisioning

Authagonal provisions users into downstream applications using the **Try-Confirm-Cancel (TCC)** pattern. This ensures that all apps agree before a user gains access, with clean rollback if any app rejects.

## When Provisioning Runs

Provisioning runs at the **authorize endpoint** (`/connect/authorize`), after the user is authenticated but before an authorization code is issued. This means:

- It runs on the user's first login through a client that requires provisioning
- Already-provisioned app/user combinations are skipped (tracked in the `UserProvisions` table)
- If provisioning fails, the authorize request returns `access_denied` — no code is issued

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
