---
layout: default
title: Bereitstellung
locale: de
---

# TCC-Bereitstellung

Authagonal stellt Benutzer in nachgelagerte Anwendungen ueber das **Try-Confirm-Cancel (TCC)**-Muster bereit. Dies stellt sicher, dass alle Apps zustimmen, bevor ein Benutzer Zugang erhaelt, mit sauberem Rollback, falls eine App ablehnt.

## Wann die Bereitstellung laeuft

Die Bereitstellung laeuft automatisch, wenn ein Benutzer erstellt wird, unabhaengig vom Erstellungspfad:

| Endpunkt | Ausloeser |
|---|---|
| `POST /api/v1/profile/` | Admin-Benutzererstellung |
| `POST /api/auth/register` | Self-Service-Registrierung |
| SAML ACS (`POST /saml/{id}/acs`) | Erster SSO-Login (neuer Benutzer) |
| OIDC-Callback (`GET /oidc/callback`) | Erster SSO-Login (neuer Benutzer) |
| SCIM (`POST /scim/v2/Users`) | Identity-Provider-Bereitstellung |
| `GET /connect/authorize` | Erste Autorisierung ueber einen Client mit `ProvisioningApps` |

Bereits bereitgestellte App/Benutzer-Kombinationen werden uebersprungen (nachverfolgt in der `UserProvisions`-Tabelle).

**Bei Ablehnung:** Wenn eine Bereitstellungs-App den Benutzer in der Try-Phase ablehnt, wird der Benutzer geloescht und der Endpunkt gibt `422 Unprocessable Entity` mit dem Ablehnungsgrund zurueck. Dies verhindert halb erstellte Benutzer.

## Konfiguration

### 1. Bereitstellungs-Apps definieren

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

### 2. Apps Clients zuweisen

Jeder Client deklariert, in welche Apps seine Benutzer bereitgestellt werden muessen:

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

Wenn sich ein Benutzer ueber `web-app` autorisiert, wird er in `my-backend` bereitgestellt, sofern dies noch nicht geschehen ist.

## TCC-Protokoll

Authagonal fuehrt drei Arten von HTTP-Aufrufen an Ihren Bereitstellungsendpunkt durch. Alle verwenden `POST` mit JSON-Koerper und `Authorization: Bearer {ApiKey}`.

### Phase 1: Versuch (Try)

**Anfrage:** `POST {CallbackUrl}/try`

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

**Erwartete Antworten:**

| Status | Antwort | Bedeutung |
|---|---|---|
| `200` | `{ "approved": true }` | Benutzer kann bereitgestellt werden. App erstellt einen **ausstehenden** Datensatz. |
| `200` | `{ "approved": false, "reason": "..." }` | Benutzer wird abgelehnt. Kein Datensatz erstellt. |
| Nicht-2xx | Beliebig | Wird als Fehler behandelt. |

Die `transactionId` identifiziert diesen Bereitstellungsversuch. Ihre App sollte sie zusammen mit dem ausstehenden Datensatz speichern.

### Phase 2: Bestaetigung (Confirm)

Wird nur aufgerufen, wenn **alle** Apps in der Versuchsphase `approved: true` zurueckgegeben haben.

**Anfrage:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `200` (beliebiger Antwortkoerper). Ihre App befoeerdert den ausstehenden Datensatz zum bestaetigten.

### Phase 3: Abbruch (Cancel)

Wird aufgerufen, wenn der Versuch **einer** App abgelehnt wurde oder fehlgeschlagen ist, um die Apps zu bereinigen, die in der Versuchsphase erfolgreich waren.

**Anfrage:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `200` (beliebiger Antwortkoerper). Ihre App loescht den ausstehenden Datensatz.

Der Abbruch erfolgt auf Best-Effort-Basis -- bei Fehlschlag protokolliert Authagonal den Fehler und faehrt fort. Ihre App sollte **unbestaetigte Datensaetze nach einer TTL bereinigen** (z.B. 1 Stunde) als Sicherheitsnetz.

## Ablaufdiagramm

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

### Bei Fehlschlag

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

### Bei teilweisem Bestaetigungsfehler

Wenn einige Bestaetigungen erfolgreich sind, aber eine fehlschlaegt, werden die erfolgreich bestaetigten Apps mit ihren Bereitstellungsdatensaetzen gespeichert (sodass sie nicht erneut versucht werden). Der Benutzer sieht einen Fehler und kann es erneut versuchen -- nur die fehlgeschlagene App wird beim naechsten Mal versucht.

## Benutzerdefinierte App-Aufloesung

Standardmaessig werden Bereitstellungs-Apps aus dem Konfigurationsabschnitt `ProvisioningApps` ueber `ConfigProvisioningAppProvider` gelesen. Ueberschreiben Sie `IProvisioningAppProvider`, um Apps dynamisch aufzuloesen -- beispielsweise aus einer Datenbank oder pro Mandant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Der Provider gibt eine Liste von Apps und deren Callback-URLs zurueck. Der `TccProvisioningOrchestrator` ruft Try/Confirm/Cancel fuer jede App auf.

## Deprovisioning

Wenn ein Benutzer ueber die Admin-API geloescht wird (`DELETE /api/v1/profile/{userId}`), ruft Authagonal `DELETE {CallbackUrl}/users/{userId}` fuer jede App auf, in der der Benutzer bereitgestellt war. Dies erfolgt auf Best-Effort-Basis -- Fehler werden protokolliert, blockieren aber nicht die Loeschung.

## Upstream-Endpunkte implementieren

### Minimales Beispiel (Node.js/Express)

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
