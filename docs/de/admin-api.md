---
layout: default
title: Admin-API
locale: de
---

# Admin-API

Admin-Endpunkte erfordern ein JWT-Zugriffstoken mit dem Bereich `authagonal-admin` (konfigurierbar über `AdminApi:Scope`).

Alle Endpunkte befinden sich unter `/api/v1/`.

## Benutzer

### Benutzer abrufen

```
GET /api/v1/profile/{userId}
```

Gibt Benutzerdetails einschließlich externer Login-Verknuepfungen zurück.

### Benutzer registrieren

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Erstellt einen Benutzer und sendet eine Verifizierungs-E-Mail. Gibt `409` zurück, wenn die E-Mail bereits vergeben ist.

### Benutzer aktualisieren

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

Alle Felder sind optional -- nur angegebene Felder werden aktualisiert. Die Änderung von `organizationId` loest aus:
- SecurityStamp-Rotation (macht alle Cookie-Sitzungen innerhalb von 30 Minuten ungültig)
- Alle Refresh-Token werden widerrufen

### Benutzer löschen

```
DELETE /api/v1/profile/{userId}
```

Löscht den Benutzer, widerruft alle Berechtigungen und deprovisioniert aus allen nachgelagerten Apps (Best-Effort).

### E-Mail bestätigen

```
POST /api/v1/profile/confirm-email?token={token}
```

### Verifizierungs-E-Mail senden

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Externe Identität verknuepfen

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Externe Identität trennen

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## MFA-Verwaltung

### MFA-Status abrufen

```
GET /api/v1/profile/{userId}/mfa
```

Gibt den MFA-Status und die registrierten Methoden eines Benutzers zurück.

### Alle MFA zurücksetzen

```
DELETE /api/v1/profile/{userId}/mfa
```

Entfernt alle MFA-Anmeldedaten und setzt `MfaEnabled=false`. Der Benutzer muss sich bei Bedarf erneut registrieren.

### Bestimmte MFA-Anmeldedaten entfernen

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Entfernt eine bestimmte MFA-Anmeldedaten (z.B. einen verlorenen Authenticator). Wenn die letzte primäre Methode entfernt wird, wird MFA deaktiviert.

## SSO-Anbieter

### SAML-Anbieter

```
POST   /api/v1/saml/connections                    # Erstellen
GET    /api/v1/saml/connections/{connectionId}     # Einzelnen abrufen
PUT    /api/v1/saml/connections/{connectionId}     # Aktualisieren
DELETE /api/v1/saml/connections/{connectionId}     # Löschen
```

### OIDC-Anbieter

```
POST   /api/v1/oidc/connections                    # Erstellen
GET    /api/v1/oidc/connections/{connectionId}     # Einzelnen abrufen
DELETE /api/v1/oidc/connections/{connectionId}     # Löschen
```

### SSO-Domaenen

```
GET    /api/v1/sso/domains                 # Alle auflisten
```

## Clients

Verwalten Sie OAuth-Clients zur Laufzeit. Alle Routen erfordern die `IdentityAdmin`-Richtlinie (den Admin-Scope).

```
GET    /api/v1/clients              # Alle Clients auflisten
GET    /api/v1/clients/{clientId}   # Einen Client abrufen
POST   /api/v1/clients              # Einen Client erstellen
PUT    /api/v1/clients/{clientId}   # Einen Client aktualisieren
DELETE /api/v1/clients/{clientId}   # Einen Client löschen
```

### Client erstellen / aktualisieren

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` gibt `409` zurück, wenn der Client bereits existiert. `PUT` aktualisiert einen vorhandenen Client (`404`, falls nicht gefunden); beim Aktualisieren werden nur neu hinzugefügte Scopes auf Eskalation geprueft.

Hinweise:

- **Geheimnis-Hashes werden niemals zurückgegeben.** `clientSecretHashes` wird aus jeder Antwort entfernt (Liste, Abrufen, Erstellen, Aktualisieren). Beim Aktualisieren bewahrt das Weglassen von `clientSecretHashes` das gespeicherte Geheimnis; das Angeben neuer Hashes rotiert es.
- **Der Admin-Scope kann keinem Client gewährt werden.** Das Anfordern von `AdminApi:Scope` (Standard `authagonal-admin`) in `allowedScopes` gibt `403 forbidden_scope` zurück — kein Client darf den Admin-Scope besitzen, andernfalls könnte ein `client_credentials`-Client unbegrenzt Admin-Token ausstellen.
- Das Hinzufügen von Scopes, die der Aufrufer nicht gewähren darf, gibt `403` zurück.

## Scopes

Verwalten Sie benutzerdefinierte OAuth-Scopes zur Laufzeit. Das vollständige Scope-Modell finden Sie unter [OAuth-Scopes](scopes).

```
GET    /api/v1/scopes           # Alle Scopes auflisten
GET    /api/v1/scopes/{name}    # Einen Scope abrufen
POST   /api/v1/scopes           # Einen Scope erstellen
PUT    /api/v1/scopes/{name}    # Einen Scope aktualisieren (nur angegebene Felder ändern sich)
DELETE /api/v1/scopes/{name}    # Einen Scope löschen
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Gibt `201` beim Erstellen zurück (`409`, wenn der Scope bereits existiert), das Scope-JSON beim Abrufen/Aktualisieren und `204` beim Löschen.

## Bereitstellungs-Apps

Verwalten Sie nachgelagerte Bereitstellungsziele zur Laufzeit. Alle Routen erfordern die `IdentityAdmin`-Richtlinie.

```
GET    /api/v1/provisioning/apps               # Apps auflisten (gibt auch das konfigurierte Limit zurück)
POST   /api/v1/provisioning/apps               # Eine App erstellen
PUT    /api/v1/provisioning/apps/{appId}       # Eine App aktualisieren
DELETE /api/v1/provisioning/apps/{appId}       # Eine App löschen
POST   /api/v1/provisioning/apps/{appId}/test  # Einen Test-/try-Aufruf an den Callback der App senden
```

### Bereitstellungs-App erstellen / aktualisieren

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` und `callbackUrl` sind erforderlich; `callbackUrl` muss eine absolute `http(s)`-URL sein.
- `tryTimeoutSeconds` wird auf den Bereich 5–300 begrenzt.
- **Der API-Schlüssel wird niemals zurückgegeben.** Antworten geben `hasApiKey` (einen Boolean) statt des Schlüssels selbst aus. Beim Aktualisieren lässt das Weglassen von `apiKey` ihn unverändert, ein leerer String löscht ihn und ein Wert ersetzt ihn.
- Die Erstellung unterliegt einem konfigurierbaren Kontingent pro Deployment (`IProvisioningAppQuota`); ein Überschreiten gibt `400 provisioning_app_limit` zurück. Die Listenantwort enthält das aktuelle `limit`.

### Eine Bereitstellungs-App testen

```
POST /api/v1/provisioning/apps/{appId}/test
```

Sendet ein synthetisches `POST {callbackUrl}/try` mit einer Beispiel-Payload (und dem API-Schlüssel der App als Bearer-Token, falls gesetzt) und gibt `{ success, statusCode, body }` zurück, damit Sie die Konnektivitaet aus der Admin-Oberflaeche verifizieren können.

## Rollen

### Rollen auflisten

```
GET /api/v1/roles
```

### Rolle abrufen

```
GET /api/v1/roles/{roleId}
```

### Rolle erstellen

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Rolle aktualisieren

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Rolle löschen

```
DELETE /api/v1/roles/{roleId}
```

### Rolle einem Benutzer zuweisen

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Rolle von einem Benutzer entfernen

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleId": "role-id"
}
```

### Rollen eines Benutzers abrufen

```
GET /api/v1/roles/user/{userId}
```

## SCIM-Token

### Token generieren

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id"
}
```

Gibt das Roh-Token einmalig zurück. Speichern Sie es sicher -- es kann nicht erneut abgerufen werden.

### Token auflisten

```
GET /api/v1/scim/tokens?clientId=client-id
```

Gibt Token-Metadaten (ID, Erstellungsdatum) ohne den Roh-Token-Wert zurück.

### Token widerrufen

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Token

### Benutzer imitieren

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Stellt Token (Access, Refresh und — wenn `openid` angefordert wird — ID-Token) im Namen eines Benutzers aus, ohne dessen Anmeldedaten zu benötigen. Nützlich für Tests und Support. Parameter werden als Query-Strings übergeben.

| Query-Parameter | Erforderlich | Beschreibung |
|---|---|---|
| `clientId` | Ja | Der Client, für den die Token ausgestellt werden. Die Token-Lebensdauern stammen aus der Konfiguration dieses Clients. |
| `userId` | Ja | Der zu imitierende Benutzer. |
| `scopes` | Nein | **Leerzeichengetrennte** Liste von Scopes (Leerzeichen URL-kodieren). Standardmäßig die `AllowedScopes` des Clients, wenn weggelassen. |

Einschraenkungen:

- Scopes sind auf die `AllowedScopes` des Clients beschraenkt — das Anfordern eines Scopes, den der Client selbst nicht anfordern könnte, gibt `400 invalid_scope` zurück.
- Der Admin-Scope (`AdminApi:Scope`, Standard `authagonal-admin`) **kann** über diesen Endpunkt **nicht** ausgestellt werden; das Anfordern gibt `403 forbidden_scope` zurück. Dies verhindert, dass ein (möglicherweise zeitlich begrenztes) Admin-Token ein langlebiges Admin-Access-/Refresh-Token erzeugt.

Die Antwort ist eine standardmäßige Token-Antwort mit `access_token`, `refresh_token`, optionalem `id_token`, `expires_in` und dem gewährten `scope` (leerzeichengetrennt).
