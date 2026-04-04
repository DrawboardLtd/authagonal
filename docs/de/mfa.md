---
layout: default
title: Multi-Faktor-Authentifizierung
locale: de
---

# Multi-Faktor-Authentifizierung (MFA)

Authagonal unterstützt Multi-Faktor-Authentifizierung für passwortbasierte Anmeldungen. Drei Methoden stehen zur Verfügung: TOTP (Authentifizierungs-Apps), WebAuthn/Passkeys (Hardware-Schlüssel und Biometrie) sowie einmalige Wiederherstellungscodes.

Verbundanmeldungen (SAML/OIDC) überspringen MFA — der externe Identitätsanbieter übernimmt die Zwei-Faktor-Authentifizierung.

## Unterstützte Methoden

| Methode | Beschreibung |
|---|---|
| **TOTP** | Zeitbasierte Einmalpasswörter (RFC 6238). Funktioniert mit jeder Authentifizierungs-App — Google Authenticator, Authy, 1Password usw. |
| **WebAuthn / Passkeys** | FIDO2-Hardware-Sicherheitsschlüssel, Plattform-Biometrie (Touch ID, Windows Hello) und synchronisierte Passkeys. |
| **Wiederherstellungscodes** | 10 einmalige Backup-Codes (Format `XXXX-XXXX`) zur Kontowiederherstellung, wenn andere Methoden nicht verfügbar sind. |

## MFA-Richtlinie

Die MFA-Erzwingung wird **pro Client** über die Eigenschaft `MfaPolicy` in `appsettings.json` konfiguriert:

| Wert | Verhalten |
|---|---|
| `Disabled` (Standard) | Keine MFA-Abfrage, auch wenn der Benutzer MFA registriert hat |
| `Enabled` | Abfrage für Benutzer mit registrierter MFA; keine erzwungene Registrierung |
| `Required` | Abfrage für registrierte Benutzer; erzwungene Registrierung für Benutzer ohne MFA |

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

Der Standardwert ist `Disabled`, sodass bestehende Clients nicht betroffen sind, bis Sie sich anmelden.

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

Weitere Informationen finden Sie unter [Erweiterbarkeit](extensibility).

## Anmeldeablauf

Der Anmeldeablauf mit MFA funktioniert wie folgt:

1. Benutzer sendet E-Mail und Passwort an `POST /api/auth/login`
2. Server überprüft das Passwort und löst dann die effektive MFA-Richtlinie auf
3. Basierend auf der Richtlinie und dem Registrierungsstatus des Benutzers:

| Richtlinie | Benutzer hat MFA? | Ergebnis |
|---|---|---|
| `Disabled` | — | Cookie gesetzt, Anmeldung abgeschlossen |
| `Enabled` | Nein | Cookie gesetzt, Anmeldung abgeschlossen |
| `Enabled` | Ja | Gibt `mfaRequired` zurück — Benutzer muss verifizieren |
| `Required` | Nein | Gibt `mfaSetupRequired` zurück — Benutzer muss sich registrieren |
| `Required` | Ja | Gibt `mfaRequired` zurück — Benutzer muss verifizieren |

### MFA-Abfrage

Wenn `mfaRequired` zurückgegeben wird, enthält die Anmeldeantwort eine `challengeId` und die verfügbaren Methoden des Benutzers. Der Client leitet zu einer MFA-Abfrageseite weiter, auf der der Benutzer mit einer seiner registrierten Methoden über `POST /api/auth/mfa/verify` verifiziert.

Abfragen laufen nach 5 Minuten ab und sind einmalig verwendbar.

### Erzwungene Registrierung

Wenn `mfaSetupRequired` zurückgegeben wird, enthält die Antwort ein `setupToken`. Dieses Token authentifiziert den Benutzer gegenüber den MFA-Einrichtungsendpunkten (über den `X-MFA-Setup-Token`-Header), sodass er eine Methode registrieren kann, bevor er eine Cookie-Sitzung erhält.

## MFA registrieren

Benutzer registrieren MFA über die Self-Service-Einrichtungsendpunkte. Diese erfordern entweder eine authentifizierte Cookie-Sitzung oder ein Setup-Token.

### TOTP-Einrichtung

1. `POST /api/auth/mfa/totp/setup` aufrufen — gibt einen QR-Code (`data:image/svg+xml;base64,...`) und ein Setup-Token zurück
2. Benutzer scannt den QR-Code mit seiner Authentifizierungs-App
3. Benutzer gibt den 6-stelligen Code zur Bestätigung ein: `POST /api/auth/mfa/totp/confirm`

### WebAuthn / Passkey-Einrichtung

1. `POST /api/auth/mfa/webauthn/setup` aufrufen — gibt `PublicKeyCredentialCreationOptions` zurück
2. Client ruft `navigator.credentials.create()` mit den Optionen auf
3. Attestierungsantwort an `POST /api/auth/mfa/webauthn/confirm` senden

### Wiederherstellungscodes

`POST /api/auth/mfa/recovery/generate` aufrufen, um 10 Einmalcodes zu generieren. Mindestens eine primäre Methode (TOTP oder WebAuthn) muss zuerst registriert sein.

Das erneute Generieren von Codes ersetzt alle vorhandenen Wiederherstellungscodes. Jeder Code kann nur einmal verwendet werden.

## MFA verwalten

### Benutzer-Self-Service

- `GET /api/auth/mfa/status` — registrierte Methoden anzeigen
- `DELETE /api/auth/mfa/credentials/{id}` — eine bestimmte Anmeldedaten entfernen

Wenn die letzte primäre Methode entfernt wird, ist MFA für den Benutzer deaktiviert.

### Admin-API

Administratoren können MFA für jeden Benutzer über die [Admin-API](admin-api) verwalten:

- `GET /api/v1/profile/{userId}/mfa` — MFA-Status eines Benutzers anzeigen
- `DELETE /api/v1/profile/{userId}/mfa` — alle MFA zurücksetzen (für gesperrte Benutzer)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — eine bestimmte Anmeldedaten entfernen

### Audit-Hook

Implementieren Sie `IAuthHook.OnMfaVerifiedAsync`, um MFA-Ereignisse zu protokollieren:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## Benutzerdefinierte Anmelde-UI

Wenn Sie eine benutzerdefinierte Anmelde-UI erstellen, behandeln Sie diese Antworten von `POST /api/auth/login`:

1. **Normale Anmeldung** — `{ userId, email, name }` mit gesetztem Cookie. Weiterleitung zu `returnUrl`.
2. **MFA erforderlich** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. MFA-Abfrageformular anzeigen.
3. **MFA-Registrierung erforderlich** — `{ mfaSetupRequired: true, setupToken }`. MFA-Registrierungsablauf anzeigen.

Weitere Informationen finden Sie in der [Auth-API](auth-api) für die vollständige Endpunktreferenz.
