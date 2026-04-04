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

Gibt Benutzerdetails einschliesslich externer Login-Verknuepfungen zurueck.

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

Erstellt einen Benutzer und sendet eine Verifizierungs-E-Mail. Gibt `409` zurueck, wenn die E-Mail bereits vergeben ist.

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

Alle Felder sind optional -- nur angegebene Felder werden aktualisiert. Die Aenderung von `organizationId` loest aus:
- SecurityStamp-Rotation (macht alle Cookie-Sitzungen innerhalb von 30 Minuten ungueltig)
- Alle Refresh-Token werden widerrufen

### Benutzer loeschen

```
DELETE /api/v1/profile/{userId}
```

Loescht den Benutzer, widerruft alle Berechtigungen und deprovisioniert aus allen nachgelagerten Apps (Best-Effort).

### E-Mail bestaetigen

```
POST /api/v1/profile/confirm-email?token={token}
```

### Verifizierungs-E-Mail senden

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Externe Identitaet verknuepfen

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Externe Identitaet trennen

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## MFA-Verwaltung

### MFA-Status abrufen

```
GET /api/v1/profile/{userId}/mfa
```

Gibt den MFA-Status und die registrierten Methoden eines Benutzers zurueck.

### Alle MFA zuruecksetzen

```
DELETE /api/v1/profile/{userId}/mfa
```

Entfernt alle MFA-Anmeldedaten und setzt `MfaEnabled=false`. Der Benutzer muss sich bei Bedarf erneut registrieren.

### Bestimmte MFA-Anmeldedaten entfernen

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Entfernt eine bestimmte MFA-Anmeldedaten (z.B. einen verlorenen Authenticator). Wenn die letzte primaere Methode entfernt wird, wird MFA deaktiviert.

## SSO-Anbieter

### SAML-Anbieter

```
GET    /api/v1/sso/saml                    # Alle auflisten
GET    /api/v1/sso/saml/{connectionId}     # Einzelnen abrufen
POST   /api/v1/sso/saml                    # Erstellen
PUT    /api/v1/sso/saml/{connectionId}     # Aktualisieren
DELETE /api/v1/sso/saml/{connectionId}     # Loeschen
```

### OIDC-Anbieter

```
GET    /api/v1/sso/oidc                    # Alle auflisten
GET    /api/v1/sso/oidc/{connectionId}     # Einzelnen abrufen
POST   /api/v1/sso/oidc                    # Erstellen
PUT    /api/v1/sso/oidc/{connectionId}     # Aktualisieren
DELETE /api/v1/sso/oidc/{connectionId}     # Loeschen
```

### SSO-Domaenen

```
GET    /api/v1/sso/domains                 # Alle auflisten
GET    /api/v1/sso/domains/{domain}        # Einzelne abrufen
POST   /api/v1/sso/domains                 # Erstellen
DELETE /api/v1/sso/domains/{domain}        # Loeschen
```

## Token

### Benutzer imitieren

```
POST /api/v1/tokens/impersonate
Content-Type: application/json

{
  "userId": "user-id",
  "clientId": "client-id",
  "scopes": ["openid", "profile"]
}
```

Stellt Token im Namen eines Benutzers aus, ohne dessen Anmeldedaten zu benoetigen. Nuetzlich fuer Tests und Support.
