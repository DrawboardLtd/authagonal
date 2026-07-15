---
layout: default
title: Admin-API
locale: de
---

# Admin-API

Admin-Endpunkte erfordern ein JWT-Zugriffstoken mit dem Scope `authagonal-admin` (konfigurierbar über `AdminApi:Scope`).

Alle Endpunkte befinden sich unter `/api/v1/`.

## Bootstrapping des ersten Admin-Tokens

Jeder `/api/v1/*`-Endpunkt erfordert ein Bearer-Token mit dem Admin-Scope. Die Admin-API selbst (und die [dynamische Client-Registrierung](client-registration)) **verweigert jedoch das Erstellen oder Aktualisieren jedes Clients mit diesem Scope** (`403 forbidden_scope`), sodass ein zur Laufzeit erstellter Client niemals zum Admin eskalieren kann. Der einzige Weg, ein Admin-Token auszustellen, ist ein **konfigurationsseitig vordefinierter Client**: Einträge im Konfigurationsabschnitt `Clients:` werden beim Start vom `ClientSeedService` angelegt oder aktualisiert (Upsert). Der Konfiguration wird vertraut, der Schutz vor dem verbotenen Scope gilt nur für die Laufzeit-APIs.

Legen Sie in `appsettings.json` (oder den entsprechenden Umgebungsvariablen bzw. dem Secret-Store) einen `client_credentials`-Client mit dem Admin-Scope an:

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` wird beim Start gehasht; geben Sie stattdessen `SecretHashes` an, wenn Sie in der Konfiguration nur einen vorab gehashten Wert vorhalten möchten. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` werden als Aliase für `Id`/`Name`/`GrantTypes`/`Scopes` akzeptiert.)

Tauschen Sie die Anmeldedaten anschließend am Standard-Token-Endpunkt gegen ein Token ein:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

Der `client_credentials`-Grant validiert den angeforderten Scope gegen die `AllowedScopes` des Clients: Da der vordefinierte Client `authagonal-admin` besitzt, wird das Token ausgestellt. Verwenden Sie es als `Authorization: Bearer {access_token}` bei jedem Admin-Aufruf:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Bewahren Sie das Secret des vordefinierten Clients in Ihrem Secret-Store auf; das Rotieren ist eine Konfigurationsänderung plus Neustart.

## Benutzer

### Benutzer abrufen

```
GET /api/v1/profile/{userId}
```

Gibt Benutzerdetails einschließlich externer Login-Verknüpfungen zurück.

### Benutzer vorhanden

```
GET /api/v1/profile/{userId}/exists
```

Gibt `204` zurück, wenn der Benutzer existiert, andernfalls `404` (eine kostengünstige Existenzprüfung, ohne Antworttext).

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

Erstellt einen Benutzer und sendet eine Verifizierungs-E-Mail. Gibt `409 user_exists` zurück, wenn die E-Mail-Adresse bereits vergeben ist.

Optionale, nur für Admins verfügbare Felder: `userId` (vom Aufrufer angegebene ID, bei Kollision `409 user_id_in_use`), `emailConfirmed` (legt den Benutzer bereits verifiziert an und überspringt die Verifizierungs-E-Mail), `companyName`, `organizationId`, `phone`, `locale` sowie `customAttributes` (eine String-Map, die beim Benutzer gespeichert und an Bereitstellungsziele weitergeleitet wird).

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

`userId` ist erforderlich, alle anderen Felder sind optional: Es werden nur die angegebenen Felder aktualisiert. Das Ändern von `organizationId` löst Folgendes aus:
- SecurityStamp-Rotation (macht alle Cookie-Sitzungen innerhalb von 30 Minuten ungültig)
- Alle Refresh Token werden widerrufen

### Benutzer löschen

```
DELETE /api/v1/profile/{userId}
```

Löscht den Benutzer, widerruft alle Grants und deprovisioniert ihn aus allen nachgelagerten Apps (Best-Effort).

### E-Mail bestätigen

```
POST /api/v1/profile/confirm-email?token={token}
```

### Verifizierungs-E-Mail senden

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Externe Identität verknüpfen

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

Entfernt bestimmte MFA-Anmeldedaten (z. B. einen verlorenen Authenticator). Wenn die letzte primäre Methode entfernt wird, wird MFA deaktiviert.

## SSO-Anbieter

### SAML-Anbieter

```
POST   /api/v1/saml/connections                    # Erstellen
GET    /api/v1/saml/connections/{connectionId}     # Einzelne abrufen
PUT    /api/v1/saml/connections/{connectionId}     # Aktualisieren (nur angegebene Felder ändern sich)
DELETE /api/v1/saml/connections/{connectionId}     # Löschen
```

Beim Erstellen sind `connectionName`, `entityId` sowie **genau eines von** `metadataLocation` (eine Metadaten-URL) oder `metadataXml` (eingefügte IdP-Metadaten, für IdPs ohne Metadaten-URL, werden beim Speichern parse-validiert und verdichtet) erforderlich. Optional: `nameIdFormat` (weglassen für den Standard `emailAddress`, `"none"`, um die NameIDPolicy wegzulassen, empfohlen für ADFS, oder eine NameID-Format-URN), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`. Jede Verbindung erhält ein serverseitig generiertes SP-Schlüsselpaar; dieses wird von der API niemals zurückgegeben. Details siehe [SAML](saml).

### OIDC-Anbieter

```
POST   /api/v1/oidc/connections                    # Erstellen
GET    /api/v1/oidc/connections/{connectionId}     # Einzelne abrufen
DELETE /api/v1/oidc/connections/{connectionId}     # Löschen
```

Beim Erstellen sind `connectionName`, `metadataLocation`, `clientId`, `clientSecret` und `redirectUrl` erforderlich. Optional: `iconUrl`, `allowedDomains`, `passthroughParams`. Das Client-Secret wird verschlüsselt gespeichert und niemals zurückgegeben. Siehe [OIDC Federation](oidc-federation).

### SSO-Domänen

```
GET    /api/v1/sso/domains                 # Alle auflisten
```

## Clients

Verwalten Sie OAuth-Clients zur Laufzeit. Alle Routen erfordern die Richtlinie `IdentityAdmin` (den Admin-Scope).

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

`POST` gibt `409` zurück, wenn der Client bereits existiert. `PUT` aktualisiert einen vorhandenen Client (`404`, falls nicht gefunden); beim Aktualisieren werden nur neu hinzugefügte Scopes auf Eskalation geprüft.

Hinweise:

- **Secret-Hashes werden niemals zurückgegeben.** `clientSecretHashes` wird aus jeder Antwort entfernt (Liste, Abrufen, Erstellen, Aktualisieren). Beim Aktualisieren bewahrt das Weglassen von `clientSecretHashes` das gespeicherte Secret; das Angeben neuer Hashes rotiert es.
- **Der Admin-Scope kann keinem Client gewährt werden.** Das Anfordern von `AdminApi:Scope` (Standard `authagonal-admin`) in `allowedScopes` gibt `403 forbidden_scope` zurück: Kein Client darf den Admin-Scope besitzen, da sonst ein `client_credentials`-Client unbegrenzt Admin-Token ausstellen könnte.
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

Gibt beim Erstellen `201` zurück (`409`, wenn der Scope bereits existiert), beim Abrufen/Aktualisieren das Scope-JSON und beim Löschen `204`.

## Bereitstellungs-Apps

Verwalten Sie nachgelagerte Bereitstellungsziele zur Laufzeit. Alle Routen erfordern die Richtlinie `IdentityAdmin`.

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
- **Der API-Schlüssel wird niemals zurückgegeben.** Antworten enthalten `hasApiKey` (einen Boolean) anstelle des Schlüssels selbst. Beim Aktualisieren lässt das Weglassen von `apiKey` ihn unverändert, ein leerer String löscht ihn, und ein Wert ersetzt ihn.
- Die Erstellung unterliegt einem konfigurierbaren Kontingent pro Deployment (`IProvisioningAppQuota`); ein Überschreiten gibt `400 provisioning_app_limit` zurück. Die Listenantwort enthält das aktuelle `limit`.

### Eine Bereitstellungs-App testen

```
POST /api/v1/provisioning/apps/{appId}/test
```

Sendet ein synthetisches `POST {callbackUrl}/try` mit einer Beispiel-Payload (und dem API-Schlüssel der App als Bearer-Token, falls gesetzt) und gibt `{ success, statusCode, body }` zurück, damit Sie die Konnektivität über die Admin-Oberfläche überprüfen können.

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
  "roleName": "admin"
}
```

Die Zuweisung erfolgt über den **Rollennamen**, nicht über die Rollen-ID. Gibt die aktualisierte Rollenliste des Benutzers zurück.

### Rolle von einem Benutzer entfernen

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
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
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` und `expiresInDays` sind optional (lassen Sie `expiresInDays` weg, um ein nicht ablaufendes Token zu erhalten). Gibt das Roh-Token einmalig zurück. Bewahren Sie es sicher auf: Es kann nicht erneut abgerufen werden.

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

Stellt Token aus (Access, Refresh und, sofern `openid` angefordert wird, ID-Token) im Namen eines Benutzers, ohne dessen Anmeldedaten zu benötigen. Nützlich für Tests und Support. Parameter werden als Query-Strings übergeben.

| Query-Parameter | Erforderlich | Beschreibung |
|---|---|---|
| `clientId` | Ja | Der Client, für den die Token ausgestellt werden. Die Token-Lebensdauern stammen aus der Konfiguration dieses Clients. |
| `userId` | Ja | Der zu imitierende Benutzer. |
| `scopes` | Nein | **Leerzeichengetrennte** Liste von Scopes (Leerzeichen URL-kodieren). Wenn weggelassen, werden standardmäßig die `AllowedScopes` des Clients verwendet. |

Einschränkungen:

- Scopes sind auf die `AllowedScopes` des Clients beschränkt: Das Anfordern eines Scopes, den der Client selbst nicht anfordern könnte, gibt `400 invalid_scope` zurück.
- Der Admin-Scope (`AdminApi:Scope`, Standard `authagonal-admin`) **kann** über diesen Endpunkt **nicht** ausgestellt werden; das Anfordern gibt `403 forbidden_scope` zurück. Dies verhindert, dass ein (möglicherweise zeitlich begrenztes) Admin-Token ein langlebiges Admin-Access-/Refresh-Token erzeugt.

Die Antwort ist eine standardmäßige Token-Antwort mit `access_token`, `refresh_token`, optionalem `id_token`, `expires_in` und dem gewährten `scope` (leerzeichengetrennt).
