---
layout: default
title: Multi-Faktor-Authentifizierung
locale: de
---

# Multi-Faktor-Authentifizierung (MFA)

Authagonal unterstützt Multi-Faktor-Authentifizierung. Drei Methoden stehen zur Verfügung: TOTP (Authentifizierungs-Apps), WebAuthn/Passkeys (Hardware-Schlüssel und Biometrie) sowie einmalige Wiederherstellungscodes. Passkeys können auch für die [passwortlose Anmeldung](#passwordless-passkey-login) verwendet werden.

Verbundanmeldungen (SAML/OIDC) werden ebenfalls abgedeckt: Eine SAML- oder OIDC-Assertion belegt den ersten Faktor, nicht den zweiten. Ein Verbundbenutzer mit registrierter MFA wird durch dieselbe lokale MFA-Abfrage geleitet wie bei einer Passwortanmeldung, und eine `Required`-Richtlinie erzwingt die Registrierung, bevor eine Sitzung ausgestellt wird. Nur wenn MFA weder registriert noch erforderlich ist, steht die Verbundanmeldung allein.

## Unterstützte Methoden

| Methode | Beschreibung |
|---|---|
| **TOTP** | Zeitbasierte Einmalpasswörter (RFC 6238): 6 Ziffern, 30-Sekunden-Schritt, SHA-1, verifiziert mit einem Ein-Schritt-Zeitversatzfenster. Funktioniert mit jeder Authentifizierungs-App (Google Authenticator, Authy, 1Password usw.). Ein bereits akzeptierter Code kann innerhalb seines Gültigkeitsfensters nicht erneut verwendet werden. |
| **WebAuthn / Passkeys** | FIDO2-Hardware-Sicherheitsschlüssel, Plattform-Biometrie (Touch ID, Windows Hello) und synchronisierte Passkeys. Benutzer können mehrere Passkeys registrieren, und Passkeys ermöglichen eine passwortlose Anmeldung. |
| **Wiederherstellungscodes** | 10 einmalige Backup-Codes (Format `XXXX-XXXX`) zur Kontowiederherstellung, wenn andere Methoden nicht verfügbar sind. Gehasht und verschlüsselt gespeichert. |

## MFA-Richtlinie

Die MFA-Erzwingung wird **pro Client** über die Eigenschaft `MfaPolicy` in `appsettings.json` konfiguriert:

| Wert | Verhalten |
|---|---|
| `Disabled` (Standard) | Keine erzwungene Registrierung; die Self-Service-Einrichtungsoberfläche blendet MFA aus, wenn jeder Client auf `Disabled` steht |
| `Enabled` | MFA-Registrierung anbieten; nicht erzwingen |
| `Required` | Registrierung für Benutzer ohne MFA erzwingen |

Ein Benutzer mit registrierter MFA wird **immer bei der Anmeldung abgefragt, unabhängig von der Client-Richtlinie**. MFA ist eine Eigenschaft des Benutzers und seiner Sitzung, nicht des anfragenden Clients, sodass eine über einen `Disabled`-Client geleitete Anfrage nicht dazu genutzt werden kann, den zweiten Faktor eines registrierten Benutzers zu umgehen.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

Der Standardwert ist `Disabled`, sodass bestehende Clients unverändert bleiben, bis Sie sich dafür entscheiden.

### Benutzerspezifische Überschreibung

Implementieren Sie `IAuthHook.ResolveMfaPolicyAsync`, um die Client-Richtlinie für bestimmte Benutzer zu überschreiben:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

Die aufgelöste Richtlinie steuert die Registrierung (ob sie angeboten oder erzwungen wird). Sie befreit einen bereits registrierten Benutzer nicht von der Abfrage; registrierte Benutzer werden immer abgefragt.

Siehe [Erweiterbarkeit](extensibility) für die vollständige Hook-Dokumentation.

## Anmeldeablauf

Der Anmeldeablauf mit MFA funktioniert wie folgt:

1. Der Benutzer sendet E-Mail und Passwort an `POST /api/auth/login`
2. Der Server überprüft das Passwort und löst dann die effektive MFA-Richtlinie auf
3. Basierend auf der Richtlinie und dem Registrierungsstatus des Benutzers:

| Richtlinie | Benutzer hat MFA? | Ergebnis |
|---|---|---|
| Beliebig | Ja | Gibt `mfaRequired` zurück: Benutzer muss verifizieren |
| `Disabled` / `Enabled` | Nein | Cookie gesetzt, Anmeldung abgeschlossen |
| `Required` | Nein | Gibt `mfaSetupRequired` zurück: Benutzer muss sich registrieren |

### MFA-Abfrage

Wenn `mfaRequired` zurückgegeben wird, enthält die Anmeldeantwort eine `challengeId`, die verfügbaren `methods` des Benutzers und (wenn der Benutzer Passkeys besitzt) `webAuthn`-Assertion-Optionen. Der Client leitet zu einer MFA-Abfrageseite weiter, auf der der Benutzer mit einer seiner registrierten Methoden über `POST /api/auth/mfa/verify` verifiziert:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` ist `totp`, `recovery` oder `webauthn` (WebAuthn sendet eine `assertion` anstelle eines `code`).

Abfragen laufen nach 5 Minuten ab (konfigurierbar über `Auth:MfaChallengeExpiryMinutes`) und werden bei erfolgreicher Verifizierung verbraucht.

#### Retry-Budget

Ein falscher Code verbraucht die Abfrage nicht. Der Verify-Endpunkt validiert den Code zuerst und verbraucht die Abfrage nur bei Erfolg, sodass eine falsch eingegebene TOTP-Ziffer einfach mit derselben `challengeId` erneut versucht werden kann. Fehlgeschlagene Versuche geben `invalid_code` (oder `assertion_failed` bei WebAuthn) mit einem 401 zurück und erhöhen einen begrenzten Zähler auf der Abfrage; der fünfte fehlgeschlagene Versuch verbraucht die Abfrage und gibt `too_many_attempts` zurück, was eine neue Anmeldung erzwingt. Dies gilt für alle drei Methoden und begrenzt TOTP-Brute-Force auf 5 Versuche pro Abfrage.

Eine fehlende, abgelaufene oder bereits verbrauchte Abfrage gibt `invalid_challenge` zurück.

### Verbundanmeldungen

Nach einer erfolgreichen SAML- oder OIDC-Assertion löst der Server dieselbe effektive MFA-Richtlinie auf. Ein Benutzer mit registrierter MFA wird zur gehosteten MFA-Abfrageseite weitergeleitet (mit einer `challengeId`), anstatt eine Sitzung zu erhalten; ein Benutzer ohne MFA unter einer `Required`-Richtlinie wird zur MFA-Einrichtungsseite weitergeleitet (mit einem `setupToken`). Die Sitzung wird erst als MFA-authentifiziert markiert, sobald die Verifizierung abgeschlossen ist.

### Erzwungene Registrierung

Wenn `mfaSetupRequired` zurückgegeben wird, enthält die Antwort ein `setupToken`. Dieses Token authentifiziert den Benutzer gegenüber den MFA-Einrichtungsendpunkten (über den `X-MFA-Setup-Token`-Header), sodass er eine Methode registrieren kann, bevor er eine Cookie-Sitzung erhält. Setup-Tokens laufen nach 15 Minuten ab (konfigurierbar über `Auth:MfaSetupTokenExpiryMinutes`).

## MFA registrieren

Benutzer registrieren MFA über die Self-Service-Einrichtungsendpunkte. Diese erfordern entweder eine authentifizierte Cookie-Sitzung oder ein Setup-Token.

### TOTP-Einrichtung

1. `POST /api/auth/mfa/totp/setup` aufrufen: gibt einen QR-Code (`data:image/png;base64,...`), einen `manualKey` (Base32 für manuelle Eingabe) und ein Setup-Token zurück
2. Der Benutzer scannt den QR-Code mit seiner Authentifizierungs-App
3. Der Benutzer gibt zur Bestätigung den 6-stelligen Code ein: `POST /api/auth/mfa/totp/confirm`

### WebAuthn / Passkey-Einrichtung

1. `POST /api/auth/mfa/webauthn/setup` aufrufen: gibt ein `setupToken` und `PublicKeyCredentialCreationOptions` zurück
2. Der Client ruft `navigator.credentials.create()` mit den Optionen auf
3. Die Attestierungsantwort an `POST /api/auth/mfa/webauthn/confirm` senden

Die Passkey-Registrierung erfordert zuerst eine bestätigte TOTP-Anmeldeinformation (`totp_required_first`). Passkeys sind eine geräteabhängige Komfortschicht über einem portablen Basisfaktor, sodass jedes Konto einen geräteunabhängigen Faktor behält und eine `Required`-Richtlinie nicht allein durch einen Passkey erfüllt werden kann.

Benutzer können mehrere Passkeys registrieren (einen pro Gerät). Eine bereits einem anderen Benutzer zugeordnete Credential-ID wird abgelehnt (`credential_already_registered`), und Benutzer, deren E-Mail-Domäne über erzwungenes SSO zu einem externen Identitätsanbieter geleitet wird, können keinen lokalen Passkey registrieren (`sso_managed`), da dies den Identitätsanbieter und seine Deprovisionierung umgehen würde.

### Wiederherstellungscodes

`POST /api/auth/mfa/recovery/generate` aufrufen, um 10 Einmalcodes zu generieren. Mindestens eine primäre Methode (TOTP oder WebAuthn) muss zuvor registriert sein.

Das erneute Generieren von Codes ersetzt alle vorhandenen Wiederherstellungscodes. Jeder Code kann nur einmal verwendet werden; ein eingelöster Code wird als verbraucht markiert und nicht mehr akzeptiert.

Codes werden nie im Klartext gespeichert: Jeder Code wird gehasht, und der Hash wird zusätzlich mit dem Secret-Provider des Mandanten verschlüsselt gespeichert, sodass ein Speicherauszug Chiffretext statt eines offline per Brute-Force angreifbaren Hashes liefert.

## Passwortlose Passkey-Anmeldung

Passkeys sind nicht nur ein zweiter Faktor: Ein Benutzer mit registriertem Passkey kann sich ohne Passwort anmelden.

1. `POST /api/auth/mfa/passwordless/begin` gibt eine `challengeId` und Assertion-`options` für erkennbare Anmeldeinformationen zurück, sodass der Authenticator jeden auf dem Gerät gespeicherten Passkey für die Seite anbietet
2. Der Client ruft `navigator.credentials.get()` mit den Optionen auf
3. `POST /api/auth/mfa/passwordless/complete` mit `{ challengeId, assertion }`: Der Server ermittelt den Benutzer allein aus dem Passkey und meldet ihn an

Die gehostete Login-Seite verdrahtet dies über bedingte Vermittlung (Passkey-Autofill) in das E-Mail-Feld: Wenn der Browser dies unterstützt, wird ein verfügbarer Passkey als Autofill-Vorschlag angeboten, ganz ohne zusätzliche Oberflächenelemente.

Ein Passkey ist phishing-resistente starke Authentifizierung, sodass die resultierende Sitzung den MFA-Marker trägt und nicht erneut abgefragt wird. Wenn die E-Mail-Domäne des Benutzers über erzwungenes SSO zu einem externen Identitätsanbieter geleitet wird, wird die passwortlose Anmeldung mit einer 409-`sso_required`-Antwort abgelehnt, die die SSO-Weiterleitungs-URL enthält, sodass ein lokaler Passkey den Identitätsanbieter nicht umgehen kann.

## MFA verwalten

### Benutzer-Self-Service

- `GET /api/auth/mfa/status`: registrierte Methoden anzeigen (meldet auch, ob MFA von irgendeinem Client angeboten wird)
- `DELETE /api/auth/mfa/credentials/{id}`: eine bestimmte Anmeldeinformation entfernen

Das Entfernen einer Anmeldeinformation erfordert eine echte authentifizierte Sitzung; ein Setup-Token autorisiert hier nur das Hinzufügen eines ersten Faktors und erhält `session_required`, sodass ein durchgesickertes Setup-Token die MFA eines Benutzers nicht herabstufen kann.

Wenn die letzte primäre Methode entfernt wird, wird MFA für den Benutzer deaktiviert.

### Admin-API

Administratoren können MFA für jeden Benutzer über die [Admin-API](admin-api) verwalten:

- `GET /api/v1/profile/{userId}/mfa`: MFA-Status eines Benutzers anzeigen
- `DELETE /api/v1/profile/{userId}/mfa`: alle MFA zurücksetzen (für gesperrte Benutzer)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: eine bestimmte Anmeldeinformation entfernen

### Audit-Hooks

Implementieren Sie `IAuthHook.OnMfaVerifiedAsync`, um MFA-Ereignisse zu protokollieren:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Der gesamte MFA-Lebenszyklus ist über Hooks abbildbar: `OnMfaVerifyFailedAsync` (ein fehlgeschlagener Verifizierungsversuch), `OnMfaEnrolledAsync` (eine Methode bestätigt), `OnMfaCredentialRemovedAsync` (eine Anmeldeinformation entfernt, mit einem Kennzeichen dafür, ob dies MFA deaktiviert hat) und `OnRecoveryCodesRegeneratedAsync`.

## Benutzerdefinierte Anmelde-UI

Wenn Sie eine benutzerdefinierte Anmelde-UI erstellen, behandeln Sie diese Antworten von `POST /api/auth/login`:

1. **Normale Anmeldung**: `{ userId, email, name }` mit gesetztem Cookie. Weiterleitung zu `returnUrl`.
2. **MFA erforderlich**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. MFA-Abfrageformular anzeigen.
3. **MFA-Registrierung erforderlich**: `{ mfaSetupRequired: true, setupToken }`. MFA-Registrierungsablauf anzeigen.

Beim Behandeln von Fehlern bei `POST /api/auth/mfa/verify`: `invalid_code` und `assertion_failed` können gegen dieselbe `challengeId` erneut versucht werden (bis zum Versuchsbudget); `too_many_attempts` und `invalid_challenge` sind endgültig, sodass der Benutzer zum Anmeldeformular zurückgeschickt werden sollte.

Siehe [Auth-API](auth-api) für die vollständige Endpunktreferenz.
