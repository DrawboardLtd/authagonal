---
layout: default
title: Auth-API
locale: de
---

# Auth-API

Diese Endpunkte betreiben die Login-SPA. Sie verwenden Cookie-Authentifizierung (`SameSite=Lax`, `HttpOnly`).

Wenn Sie eine benutzerdefinierte Login-Oberflaeche erstellen, sind dies die Endpunkte, gegen die Sie implementieren muessen.

## Endpunkte

### Anmelden

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Erfolg (200):** Setzt ein Auth-Cookie und gibt zurueck:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

**MFA erforderlich (200):** Wenn der Benutzer MFA registriert hat und die `MfaPolicy` des Clients `Enabled` oder `Required` ist:

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Der Client sollte zu einer MFA-Abfrageseite weiterleiten und `POST /api/auth/mfa/verify` aufrufen.

**MFA-Einrichtung erforderlich (200):** Wenn `MfaPolicy` auf `Required` gesetzt ist und der Benutzer keine MFA registriert hat:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Der Client sollte zu einer MFA-Einrichtungsseite weiterleiten. Das Setup-Token authentifiziert den Benutzer bei den MFA-Setup-Endpunkten ueber den `X-MFA-Setup-Token`-Header.

**Fehlerantworten:**

| `error` | Status | Beschreibung |
|---|---|---|
| `invalid_credentials` | 401 | Falsche E-Mail oder falsches Passwort |
| `locked_out` | 423 | Zu viele fehlgeschlagene Versuche. `retryAfter` (Sekunden) ist enthalten. |
| `email_not_confirmed` | 403 | E-Mail noch nicht verifiziert |
| `sso_required` | 403 | Domain erfordert SSO. `redirectUrl` verweist auf die SSO-Anmeldung. |
| `email_required` | 400 | E-Mail-Feld ist leer |
| `password_required` | 400 | Passwort-Feld ist leer |

### Abmelden

```
POST /api/auth/logout
```

Loescht das Auth-Cookie. Gibt `200 { success: true }` zurueck.

### Passwort vergessen

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Gibt immer `200` zurueck (Anti-Enumeration). Wenn der Benutzer existiert, wird eine Zuruecksetzungs-E-Mail gesendet.

### Passwort zuruecksetzen

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Beschreibung |
|---|---|
| `weak_password` | Erfuellt nicht die Staerkeanforderungen |
| `invalid_token` | Token ist fehlerhaft |
| `token_expired` | Token ist abgelaufen (24 Stunden Gueltigkeit) |

### Sitzung

```
GET /api/auth/session
```

Gibt aktuelle Sitzungsinformationen zurueck, wenn authentifiziert:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Gibt `401` zurueck, wenn nicht authentifiziert.

### SSO-Pruefung

```
GET /api/auth/sso-check?email=user@acme.com
```

Prueft, ob die E-Mail-Domain SSO erfordert:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Wenn SSO nicht erforderlich ist:

```json
{
  "ssoRequired": false
}
```

### Passwortrichtlinie

```
GET /api/auth/password-policy
```

Gibt die Passwortanforderungen des Servers zurueck (konfiguriert ueber `PasswordPolicy` in den Einstellungen):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

Die Standard-Login-Oberflaeche ruft diesen Endpunkt auf der Passwort-zuruecksetzen-Seite ab, um Anforderungen dynamisch anzuzeigen.

## Standard-Passwortanforderungen

Mit Standardkonfiguration muessen Passwoerter alle folgenden Kriterien erfuellen:

- Mindestens 8 Zeichen
- Mindestens ein Grossbuchstabe
- Mindestens ein Kleinbuchstabe
- Mindestens eine Ziffer
- Mindestens ein nicht-alphanumerisches Zeichen
- Mindestens 2 verschiedene Zeichen

Diese koennen ueber den Konfigurationsabschnitt `PasswordPolicy` angepasst werden -- siehe [Konfiguration](configuration).

## MFA-Endpunkte

### MFA verifizieren

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifiziert eine MFA-Abfrage. Bei Erfolg wird das Auth-Cookie gesetzt und Benutzerinformationen zurueckgegeben.

**Methoden:**

| `method` | Erforderliche Felder | Beschreibung |
|---|---|---|
| `totp` | `code` (6 Ziffern) | Zeitbasiertes Einmalpasswort aus einer Authenticator-App |
| `webauthn` | `assertion` (JSON-String) | WebAuthn-Assertion-Antwort von `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Einmal-Wiederherstellungscode (wird bei Verwendung verbraucht) |

### MFA-Status

```
GET /api/auth/mfa/status
```

Gibt die registrierten MFA-Methoden des Benutzers zurueck. Erfordert Cookie-Authentifizierung oder den `X-MFA-Setup-Token`-Header.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

### TOTP-Einrichtung

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/svg+xml;base64,..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### WebAuthn / Passkey-Einrichtung

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

### Wiederherstellungscodes

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Generiert 10 Einmal-Wiederherstellungscodes. Erfordert, dass mindestens eine primaere Methode (TOTP oder WebAuthn) registriert ist. Eine Neugenerierung ersetzt alle bestehenden Wiederherstellungscodes.

### MFA-Anmeldedaten entfernen

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Entfernt eine bestimmte MFA-Anmeldedaten. Wenn die letzte primaere Methode entfernt wird, wird MFA fuer den Benutzer deaktiviert.

## Benutzerdefinierte Login-Oberflaeche erstellen

Die Standard-SPA (`login-app/`) ist eine Implementierung dieser API. Um Ihre eigene zu erstellen:

1. Stellen Sie Ihre Oberflaeche unter den Pfaden `/login`, `/forgot-password`, `/reset-password` bereit
2. Der Autorisierungsendpunkt leitet nicht authentifizierte Benutzer zu `/login?returnUrl={encoded-authorize-url}` weiter
3. Nach erfolgreicher Anmeldung (Cookie gesetzt) leiten Sie den Benutzer zur `returnUrl` weiter
4. Passwort-Zuruecksetzungslinks verwenden `{Issuer}/reset-password?p={token}`

Ihre Oberflaeche muss vom **selben Ursprung** wie die API bereitgestellt werden, da:
- Cookie-Authentifizierung `SameSite=Lax` + `HttpOnly` verwendet
- Der Autorisierungsendpunkt zu `/login` weiterleitet (relativ)
- Zuruecksetzungslinks `{Issuer}/reset-password` verwenden
