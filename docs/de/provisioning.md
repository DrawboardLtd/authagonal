---
layout: default
title: Bereitstellung
locale: de
---

# TCC-Bereitstellung

Authagonal stellt Benutzer mithilfe des **Try-Confirm-Cancel (TCC)**-Musters in nachgelagerte Anwendungen bereit. Dies stellt sicher, dass alle Apps zustimmen, bevor ein Benutzer Zugriff erhält, mit sauberem Rollback, falls eine App ablehnt.

## Wann die Bereitstellung läuft

Die Bereitstellung läuft automatisch, sobald ein Benutzer erstellt wird, unabhängig vom Erstellungspfad:

| Endpunkt | Auslöser |
|---|---|
| `POST /api/v1/profile/` | Admin-Benutzererstellung |
| `POST /api/auth/register` | Self-Service-Registrierung |
| SAML ACS (`POST /saml/{id}/acs`) | Erste SSO-Anmeldung (neuer Benutzer) |
| OIDC-Callback (`GET /oidc/callback`) | Erste SSO-Anmeldung (neuer Benutzer) |
| SCIM (`POST /scim/v2/Users`) | Bereitstellung durch den Identity Provider |
| `GET /connect/authorize` | Erste Autorisierung über einen Client mit `ProvisioningApps` |

Bereits bereitgestellte App-/Benutzer-Kombinationen werden übersprungen (nachverfolgt in der Tabelle `UserProvisions`).

Die Benutzererstellungspfade stellen in **jede konfigurierte App** bereit. Der Autorisierungs-Endpunkt stellt nur in die `ProvisioningApps`-Liste des Clients bereit.

**Bei Ablehnung:** Lehnt eine Bereitstellungs-App den Benutzer in der Try-Phase ab, wird der neu erstellte Benutzer gelöscht. Dies verhindert halb erstellte Benutzer. Die API-Erstellungspfade (Admin, Registrierung, SCIM) geben `422 Unprocessable Entity` mit dem Ablehnungsgrund zurück; die SAML-/OIDC-SSO-Callbacks geben `400 Bad Request` zurück; der Autorisierungs-Endpunkt leitet mit `error=access_denied` zurück zum Client um.

## Konfiguration

### 1. Bereitstellungs-Apps definieren

In `appsettings.json`:

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

`TryTimeoutSeconds` ist optional (Standard 60). Erhöhen Sie den Wert, wenn die nachgelagerte App während der Try-Phase echte Arbeit verrichtet. Confirm und Cancel verwenden immer ein kurzes, festes Timeout (10 Sekunden) und sind nicht konfigurierbar; sie sollten stets günstig sein.

### 2. Apps Clients zuweisen

Jeder Client deklariert über das Feld `provisioningApps` im Client-Datensatz, in welche Apps seine Benutzer bereitgestellt werden müssen. Setzen Sie dies über die Client-Admin-API (die `Clients`-Seed-Konfiguration führt dieses Feld nicht):

```
PUT /api/v1/clients/web-app
{
  "clientId": "web-app",
  "provisioningApps": ["my-backend"],
  ...
}
```

Wenn sich ein Benutzer über `web-app` autorisiert, wird er in `my-backend` bereitgestellt, sofern dies noch nicht geschehen ist.

## TCC-Protokoll

Authagonal führt drei Arten von HTTP-Aufrufen an Ihren Bereitstellungsendpunkt durch. Alle verwenden `POST` mit JSON-Körpern und `Authorization: Bearer {ApiKey}`.

### Phase 1: Versuch (Try)

**Anfrage:** `POST {CallbackUrl}/try`

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

Leere Felder (einschließlich `customAttributes`, wenn der Benutzer keine hat) werden aus dem Payload weggelassen.

**Erwartete Antworten:**

| Status | Body | Bedeutung |
|---|---|---|
| `200` | `{ "approved": true }` | Benutzer kann bereitgestellt werden. App erstellt einen **ausstehenden** Datensatz. |
| `200` | `{ "approved": false, "reason": "..." }` | Benutzer wird abgelehnt. Kein Datensatz erstellt. |
| Nicht-2xx | Beliebig | Wird als Fehlschlag behandelt. |

Die `transactionId` identifiziert diesen Bereitstellungsversuch. Ihre App sollte sie zusammen mit dem ausstehenden Datensatz speichern.

Eine genehmigte Antwort kann zusätzlich `organizationId` und/oder `customAttributes` zurückgeben. Authagonal führt diese mit dem Benutzer zusammen: `organizationId` wird nur angewendet, wenn der Benutzer noch keine hat (spätere Apps innerhalb derselben Transaktion sehen die zuvor vorgenommene Zuweisung), und `customAttributes`-Einträge werden Schlüssel für Schlüssel zusammengeführt. Beide fließen in Tokens ein (Claim `org_id`; benutzerdefinierte Attribute über die Scope-Konfiguration `UserClaims`).

### Phase 2: Bestätigung (Confirm)

Wird nur aufgerufen, wenn **alle** Apps in der Try-Phase `approved: true` zurückgegeben haben.

**Anfrage:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `200` (beliebiger Body). Ihre App befördert den ausstehenden Datensatz zum bestätigten Datensatz.

### Phase 3: Abbruch (Cancel)

Wird aufgerufen, wenn der Try-Versuch **einer** App abgelehnt wurde oder fehlgeschlagen ist, um die Apps zu bereinigen, die in der Try-Phase erfolgreich waren.

**Anfrage:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `200` (beliebiger Body). Ihre App löscht den ausstehenden Datensatz.

Cancel erfolgt auf Best-Effort-Basis: Schlägt es fehl, protokolliert Authagonal den Fehler und fährt fort. Ihre App sollte **unbestätigte Datensätze nach einer TTL bereinigen** (z. B. 1 Stunde) als Sicherheitsnetz.

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

### Bei teilweisem Bestätigungsfehler

Schlagen manche Bestätigungen fehl, während andere gelingen, werden für die erfolgreich bestätigten Apps ihre Bereitstellungsdatensätze gespeichert (sodass sie nicht erneut versucht werden), und alle Apps, die noch auf die Bestätigung warten, werden abgebrochen. Der Benutzer sieht einen Fehler und kann es erneut versuchen; nur die Apps, die nicht bestätigt haben, werden beim nächsten Mal versucht.

## Benutzerdefinierte App-Auflösung

Standardmäßig werden Bereitstellungs-Apps über `ConfigProvisioningAppProvider` aus dem Konfigurationsabschnitt `ProvisioningApps` gelesen. Überschreiben Sie `IProvisioningAppProvider`, um Apps dynamisch aufzulösen, zum Beispiel aus einer Datenbank oder pro Mandant:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Der Provider gibt eine Liste von Apps und deren Callback-URLs zurück. Der `TccProvisioningOrchestrator` ruft für jede App Try/Confirm/Cancel auf.

Für Laufzeit-CRUD ohne einen benutzerdefinierten Provider liefert die Bibliothek `StoreProvisioningAppProvider`, unterstützt durch `IProvisioningAppStore`. Registrieren Sie ihn explizit (nach demselben Muster wie oben) und verwalten Sie Apps über die Admin-API unter `/api/v1/provisioning/apps` (Auflisten/Erstellen/Aktualisieren/Löschen sowie `POST /{appId}/test`, um den Try-Endpunkt einer App zu testen).

## Deprovisioning

Wird ein Benutzer über die Admin-API gelöscht (`DELETE /api/v1/profile/{userId}`) oder über SCIM deprovisioniert (`DELETE /scim/v2/Users/{id}`, ein Soft-Delete, das den Benutzer deaktiviert), ruft Authagonal `DELETE {CallbackUrl}/users/{userId}` für jede App auf, in der der Benutzer bereitgestellt war. Dies erfolgt auf Best-Effort-Basis: Fehler werden protokolliert, blockieren aber nicht die Löschung.

## Upstream-Endpunkte implementieren

### Minimales Beispiel (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Ihre Geschäftslogik: Darf dieser Benutzer bereitgestellt werden?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Ausstehenden Datensatz mit TTL speichern
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Zum echten Datensatz befördern
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Unbestätigte Datensätze bereinigen, die älter als 1 Stunde sind
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
