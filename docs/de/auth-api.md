---
layout: default
title: Auth-API
locale: de
---

# Auth-API

Diese Endpunkte betreiben die Login-SPA. Sie verwenden Cookie-Authentifizierung (`SameSite=Lax`, `HttpOnly`).

Wenn Sie eine benutzerdefinierte Login-Oberfläche erstellen, sind dies die Endpunkte, gegen die Sie implementieren müssen.

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

**Erfolg (200):** Setzt ein Auth-Cookie und gibt zurück:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` ist `true`, wenn die `MfaPolicy` des Clients auf `Enabled` steht, der Benutzer sich aber noch nicht registriert hat (die Oberfläche kann dann die Einrichtung anbieten); in diesem Fall wird zusätzlich ein Feld `clientId` mitgeliefert.

**MFA erforderlich (200):** Wenn der Benutzer MFA registriert hat, wird er **immer** herausgefordert, unabhängig von der `MfaPolicy` des anfragenden Clients (MFA ist eine Eigenschaft des Benutzers/der Sitzung, nicht des Clients):

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

Der Client sollte zu einer MFA-Einrichtungsseite weiterleiten. Das Setup-Token authentifiziert den Benutzer bei den MFA-Setup-Endpunkten über den `X-MFA-Setup-Token`-Header.

**Fehlerantworten:**

| `error` | Status | Beschreibung |
|---|---|---|
| `invalid_credentials` | 401 | Falsche E-Mail-Adresse oder falsches Passwort. Bei unbekannten E-Mail-Adressen absichtlich identisch (Anti-Enumeration). |
| `locked_out` | 423 | Zu viele fehlgeschlagene Versuche. `retryAfter` (Sekunden) ist enthalten. |
| `account_disabled` | 403 | Konto ist deaktiviert (wird erst nach einem korrekten Passwort sichtbar) |
| `email_not_confirmed` | 403 | E-Mail noch nicht bestätigt (wird erst nach einem korrekten Passwort sichtbar) |
| `sso_required` | 409 | Domäne erfordert SSO. `redirectUrl` verweist auf die SSO-Anmeldung. |
| `captcha_failed` | 400 | Turnstile-Verifizierung fehlgeschlagen (nur wenn Turnstile konfiguriert ist; Anfragen benötigen dann ein Feld `turnstileToken`) |
| `email_required` | 400 | E-Mail-Feld ist leer |
| `password_required` | 400 | Passwort-Feld ist leer |

### Registrieren

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Erstellt ein neues Benutzerkonto und sendet eine Bestätigungs-E-Mail. Gibt `201 { "success": true, "userId": "..." }` zurück. Optionale Felder: `locale` (ein BCP-47-Tag, der beim Benutzer gespeichert wird) und `customAttributes` (eine String-Map).

Die Registrierung ist absichtlich **enumerationsneutral**: Wenn die E-Mail-Adresse bereits registriert ist, ist die Antwort dieselbe neutrale `201` (mit einer Wegwerf-`userId`), und der tatsächliche Inhaber erhält stattdessen eine Anmelde-/Zurücksetzen-Benachrichtigung per E-Mail. Die Registrierung ist außerdem pro IP ratenbegrenzt: `429 rate_limited`, wenn das Limit überschritten wird (Zeitfenster und Obergrenze konfigurierbar über `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### E-Mail bestätigen

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Bestätigt die E-Mail-Adresse des Benutzers mit dem Token aus der Bestätigungs-E-Mail. `GET` ist der anklickbare Link in der E-Mail; er leitet auf `/login?email_confirmed=1` weiter (plus einen Parameter `continue_client`, wenn die Registrierung aus einem OAuth-Ablauf stammte). `POST` ist der programmatische Weg und gibt JSON zurück (das Token kann auch in einem JSON-Body als `{ "token": "..." }` übergeben werden); die Antwort enthält optional ein Feld `appLink` (Ziel für "weiter zur App").

### Anbieter

```
GET /api/auth/providers
```

Gibt die Liste der konfigurierten externen Identitätsanbieter zurück (zum Rendern von SSO-Schaltflächen):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Verbindungen mit konfigurierten `AllowedDomains` werden **ausgeschlossen**; diese werden stattdessen E-Mail-first über `/api/auth/sso-check` erreicht statt über eine Schaltfläche. `turnstileSiteKey` ist gesetzt, wenn Cloudflare Turnstile konfiguriert ist (die Login-Oberfläche muss dann bei Anmelde-/Registrierungs-/Passwort-Anfragen ein `turnstileToken` mitsenden).

### Abmelden

```
POST /api/auth/logout
```

Löscht das Auth-Cookie. Gibt `200 { success: true }` zurück.

### Passwort vergessen

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Gibt immer `200` zurück (Anti-Enumeration). Wenn der Benutzer existiert, wird eine Zurücksetzungs-E-Mail gesendet.

### Passwort zurücksetzen

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
| `weak_password` | Erfüllt nicht die Stärkeanforderungen |
| `invalid_token` | Token ist fehlerhaft |
| `token_expired` | Token ist abgelaufen (standardmäßig 60 Minuten Gültigkeit, konfigurierbar über `Auth:PasswordResetExpiryMinutes`) |

### Sitzung

```
GET /api/auth/session
```

Gibt aktuelle Sitzungsinformationen zurück, wenn authentifiziert:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Gibt `401` zurück, wenn nicht authentifiziert.

### Apps

```
GET /api/auth/apps
```

Gibt die Anwendungslinks des Mandanten für den "Zurück zur App"-Starter der Kontoseite zurück: aktivierte Clients, die eine Home-URI besitzen (`initiateLoginUri` wird gegenüber `clientUri` bevorzugt). Jeder Eintrag hat die Form `{ clientId, clientName, homeUri, logoUri, isDefault }`; genau eine App ist als Standard markiert (der markierte Client, oder der einzige Client mit einer Home-URI). Erfordert Cookie-Authentifizierung.

### Profil (Self-Service)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

Der authentifizierte Benutzer liest/aktualisiert seine eigenen, nicht sensiblen Profilfelder: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Leere (null) Felder bleiben unverändert; E-Mail, Passwort, Rollen, Aktivstatus und Organisation sind hier **nicht** bearbeitbar. Beide geben das Profil zurück: `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### SSO-Prüfung

```
GET /api/auth/sso-check?email=user@acme.com
```

Prüft, ob die E-Mail-Domäne SSO erfordert:

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

Gibt die Passwortanforderungen des Servers zurück (konfiguriert über `PasswordPolicy` in den Einstellungen):

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

Die Standard-Login-Oberfläche ruft diesen Endpunkt auf der Seite zum Zurücksetzen des Passworts ab, um die Anforderungen dynamisch anzuzeigen.

## Standard-Passwortanforderungen

Bei Standardkonfiguration müssen Passwörter alle folgenden Kriterien erfüllen:

- Mindestens 8 Zeichen
- Mindestens ein Großbuchstabe
- Mindestens ein Kleinbuchstabe
- Mindestens eine Ziffer
- Mindestens ein nicht-alphanumerisches Zeichen
- Mindestens 2 unterschiedliche Zeichen

Diese können über den Konfigurationsabschnitt `PasswordPolicy` angepasst werden, siehe [Konfiguration](configuration).

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

Verifiziert eine MFA-Abfrage. Bei Erfolg wird das Auth-Cookie gesetzt und Benutzerinformationen werden zurückgegeben.

**Methoden:**

| `method` | Erforderliche Felder | Beschreibung |
|---|---|---|
| `totp` | `code` (6 Ziffern) | Zeitbasiertes Einmalpasswort aus einer Authenticator-App |
| `webauthn` | `assertion` (JSON-String) | WebAuthn-Assertion-Antwort von `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Einmal-Wiederherstellungscode (wird bei Verwendung verbraucht) |

**Wiederholungssemantik:** Ein falscher Code verbraucht die Abfrage **nicht**; der Code wird zuerst validiert, und die Abfrage wird erst bei Erfolg verbraucht, sodass der Benutzer dieselbe `challengeId` nach einer vertippten Ziffer erneut versuchen kann (`401 invalid_code` / `assertion_failed`). Jede Abfrage toleriert **5 fehlgeschlagene Versuche**; der 5. Fehlversuch verbraucht sie und gibt `401 too_many_attempts` zurück, was eine erneute Anmeldung erzwingt (dies begrenzt TOTP-Brute-Force auf 5 Versuche pro Abfrage). Abfragen laufen außerdem ab (standardmäßig 5 Minuten, `Auth:MfaChallengeExpiryMinutes`); eine abgelaufene, unbekannte oder bereits verbrauchte `challengeId` gibt `invalid_challenge` zurück. TOTP-Codes sind zusätzlich replay-geschützt: Ein Code aus einem bereits verwendeten Zeitschritt wird abgelehnt.

### MFA-Status

```
GET /api/auth/mfa/status
```

Gibt die registrierten MFA-Methoden des Benutzers zurück. Erfordert Cookie-Authentifizierung oder den Header `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` ist `false`, wenn die `MfaPolicy` jedes Clients `Disabled` ist; der Mandant hat MFA also deaktiviert, sodass die Einrichtungsoberfläche sich selbst ausblenden kann. Wiederherstellungscode-Einträge tragen zusätzlich `isConsumed`.

### TOTP-Einrichtung

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### WebAuthn-/Passkey-Einrichtung

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

Die Passkey-Registrierung erfordert **zuerst eine bestätigte TOTP-Anmeldeinformation** (`400 totp_required_first`); Passkeys sind eine geräteweise Komfortschicht über einem portablen Basisfaktor, sodass ein Konto niemals nur-Passkey und an ein Gerät gebunden enden kann. Benutzer, deren E-Mail-Domäne SSO-geroutet ist, können keinen lokalen Passkey registrieren (`400 sso_managed`); das würde den IdP des Mandanten umgehen. Eine Anmeldeinformations-ID, die bereits bei einem anderen Benutzer registriert ist, wird mit `409 credential_already_registered` abgelehnt.

### Wiederherstellungscodes

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Generiert 10 Einmal-Wiederherstellungscodes. Erfordert, dass mindestens eine primäre Methode (TOTP oder WebAuthn) registriert ist. Eine Neugenerierung ersetzt alle bestehenden Wiederherstellungscodes.

### MFA-Anmeldeinformation entfernen

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Entfernt eine bestimmte MFA-Anmeldeinformation. Wenn die letzte primäre Methode entfernt wird, wird MFA für den Benutzer deaktiviert. Erfordert eine echte Cookie-Sitzung; ein Setup-Token wird mit `403 session_required` abgelehnt (Setup-Token existieren nur, um einen ersten Faktor hinzuzufügen, niemals um MFA herabzustufen).

### Passwortlose Passkey-Anmeldung

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Anmeldung per erkennbarer Anmeldeinformation (residenter Passkey) ohne vorherigen Benutzerkontext: `begin` stellt eine Assertion-Abfrage mit einer leeren `allowCredentials`-Liste aus, und `complete` löst den Benutzer **aus** dem gewählten Passkey auf, verifiziert die Assertion und meldet ihn an (die Sitzung trägt den MFA-Marker, da ein Passkey ein phishing-resistenter starker Faktor ist). Wenn die E-Mail-Domäne des aufgelösten Benutzers SSO-geroutet ist, wird die Anmeldung mit `409 sso_required` + `redirectUrl` abgelehnt, damit ein lokaler Passkey einen erzwungenen IdP nicht umgehen kann.

## Geräteautorisierung (RFC 8628)

### Gerätecode anfordern

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Gibt einen Gerätecode, einen Benutzercode und eine Verifizierungs-URI zurück:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` stammt aus `DeviceCodeLifetimeSeconds` des Clients (Standard 300). Das Gerät zeigt dem Benutzer die `verification_uri` und den `user_code` an und fragt den Token-Endpunkt dann mit dem `device_code` ab, nicht schneller als im Abstand von `interval` Sekunden, sonst antwortet der Token-Endpunkt mit `slow_down` (RFC 8628 §3.5). Solange der Benutzer noch nicht zugestimmt hat, gibt der Token-Endpunkt `authorization_pending` zurück. Der Benutzer ruft die Verifizierungs-URI auf, meldet sich an und gibt den Benutzercode ein, um zuzustimmen.

### Gerät genehmigen

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Erfordert Cookie-Authentifizierung. Genehmigt den Gerätecode für den aktuellen Benutzer. Das Gerät kann den Gerätecode dann über den Token-Endpunkt mit dem Grant-Typ `urn:ietf:params:oauth:grant-type:device_code` gegen Token eintauschen.

Der übermittelte Code wird vor der Suche gemäß RFC 8628 §6.1 normalisiert: Er wird in Großbuchstaben umgewandelt, und jedes Zeichen außerhalb des 31-stelligen Code-Alphabets wird verworfen. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` und ein Einfügen, bei dem aus dem Bindestrich ein Geviertstrich geworden ist, sind alle derselbe Code. Der Bindestrich existiert nur, damit sich der Code leichter vorlesen lässt. Die Eingabe ist auf zehn Versuche pro Minute und Subjekt begrenzt (RFC 8628 §5.1); der elfte gibt `429` zurück. Dieser Zähler gilt beim standardmäßigen In-Prozess-Rate-Limiter pro Knoten, ein Deployment mit mehreren Repliken sollte die Begrenzung daher zusätzlich am Edge durchsetzen.

## Token-Introspektion (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Oder mit formularcodierten Anmeldedaten:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Gibt Token-Metadaten zurück:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Inaktive oder ungültige Token geben `{ "active": false }` zurück. Unterstützt sowohl JWT-Access-Token als auch opake Refresh-Token.

## Consent-Endpunkte

### Consent-Informationen

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Gibt Client-Details und die angeforderten Scopes für die Consent-Seite zurück (`scope` ist standardmäßig `openid`, wenn nicht angegeben):

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

Gibt `404 client_not_found` für einen unbekannten Client zurück.

### Zustimmung übermitteln

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Zeichnet die Zustimmungsentscheidung des Benutzers auf (erfordert Cookie-Authentifizierung) und gibt `{ "redirect": "..." }` zurück, wohin die SPA navigieren soll. Bei Zustimmung werden die gewährten Scopes gespeichert (gefiltert auf die `AllowedScopes` des Clients; ein manipulierter Body kann keine Scopes aufzeichnen, die der Client gar nicht anfordern durfte), und die Weiterleitung führt zurück in den Autorisierungsablauf. Bei `"decision": "deny"` führt die Weiterleitung zur `redirect_uri` des Clients mit einem Fehler `access_denied`.

### Bewilligungen auflisten

```
GET /consent/grants
```

Gibt alle Anwendungen zurück, die der Benutzer autorisiert hat:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Bewilligung widerrufen

```
DELETE /consent/grants/{clientId}
```

Widerruft die Zustimmung für eine bestimmte Anwendung. Der Benutzer wird bei seiner nächsten Anmeldung zur erneuten Zustimmung aufgefordert.

## Eine benutzerdefinierte Login-Oberfläche erstellen

Die Standard-SPA (`login-app/`) ist eine Implementierung dieser API. Um Ihre eigene zu erstellen:

1. Stellen Sie Ihre Oberfläche unter den Pfaden `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device` bereit
2. Der Autorisierungsendpunkt leitet nicht authentifizierte Benutzer zu `/login?returnUrl={encoded-authorize-url}` weiter
3. Nach erfolgreicher Anmeldung (Cookie gesetzt) leiten Sie den Benutzer zur `returnUrl` weiter
4. Links zum Zurücksetzen des Passworts verwenden `{Issuer}/login/reset-password?p={token}` (die Login-SPA ist unter `/login` eingebunden)

Ihre Oberfläche muss vom **selben Origin** wie die API bereitgestellt werden, weil:
- Die Cookie-Authentifizierung `SameSite=Lax` + `HttpOnly` verwendet
- Der Autorisierungsendpunkt zu `/login` weiterleitet (relativ)
- Zurücksetzungslinks `{Issuer}/login/reset-password` verwenden
