---
layout: default
title: OIDC-Föderation
locale: de
---

# OIDC-Föderation

Authagonal kann die Authentifizierung an externe OIDC-Identitätsanbieter föderieren (Google, Apple, Azure AD usw.). Dies ermöglicht Abläufe im Stil von "Mit Google anmelden", während Authagonal der zentrale Authentifizierungsserver bleibt.

## Funktionsweise

Es gibt zwei Einstiegspfade in die Föderation:

**Domänenbasiert (interaktive Anmeldung):**

1. Der Benutzer gibt seine E-Mail-Adresse auf der Login-Seite ein
2. Die SPA ruft `/api/auth/sso-check` auf: Wenn die E-Mail-Domäne mit einem OIDC-Anbieter verknüpft ist, ist SSO erforderlich
3. Der Benutzer klickt auf "Weiter mit SSO" → wird zum externen IdP weitergeleitet
4. Nach der Authentifizierung leitet der IdP zurück zu `/oidc/callback`
5. Authagonal validiert das id_token, erstellt/verknüpft den Benutzer und setzt ein Sitzungs-Cookie

**RP-Hinweis (`idp_hint`):**

Die nachgelagerte Relying Party kann direkt zu einem bestimmten vorgelagerten IdP weiterleiten, ohne den Schritt über E-Mail/SSO-Domäne zu durchlaufen. Hängen Sie `idp_hint={connectionId}` an `/connect/authorize` an:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Wenn die Anfrage nicht authentifiziert ist, leitet Authagonal zu `/oidc/{connectionId}/login` weiter, wobei die ursprüngliche `/authorize`-URL als `returnUrl` erhalten bleibt. Nach Abschluss der Föderation landet der Benutzer wieder bei `/authorize` mit einem Sitzungs-Cookie, und der Ablauf setzt sich normal fort.

## Einrichtung

### 1. Einen OIDC-Anbieter erstellen

**Option A: Konfiguration (empfohlen für statische Setups):**

Zu `appsettings.json` hinzufügen:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Anbieter werden beim Start initialisiert. Die initialisierbaren Felder sind genau die gezeigten: `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `RedirectUrl`, `AllowedDomains`. Das `ClientSecret` wird über `ISecretProvider` geschützt (Key Vault, sofern konfiguriert, sonst Klartext). SSO-Domänenzuordnungen werden automatisch aus `AllowedDomains` registriert.

Das Verbindungsmodell trägt zusätzliches optionales Verhalten: `PassthroughParams` (einstellbar über die Erstellung per Admin-API) sowie `SessionExpClaim` und `DisableJitProvisioning` (Felder auf Store-Ebene, gesetzt über `IOidcProviderStore` aus dem Hosting-Code); siehe [Scope- und Claim-Durchreichung](#scope-and-claim-flow-through) und [Sitzungsdauer-Obergrenze](#session-lifetime-cap) weiter unten.

**Option B: Admin-API (für Laufzeitverwaltung):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. SSO-Domänenrouting

Wenn `AllowedDomains` angegeben ist (in der Konfiguration oder über die Create-API), werden SSO-Domänenzuordnungen automatisch registriert. Ohne Domänenrouting können Benutzer weiterhin über `/oidc/{connectionId}/login` zum OIDC-Login geleitet werden.

## Endpunkte

| Endpunkt | Beschreibung |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initiiert den OIDC-Login. Generiert PKCE + State + Nonce, leitet den vorgelagerten Scope und die Passthrough-Parameter aus `returnUrl` ab und leitet zum Autorisierungsendpunkt des IdP weiter. |
| `GET /oidc/callback` | Verarbeitet den IdP-Callback. Tauscht den Code gegen Token, validiert das id_token, erfasst jeden Nicht-Protokoll-Claim im Cookie als `federated:*` und erstellt/meldet den Benutzer an. |

## Scope- und Claim-Durchreichung

Der von der nachgelagerten RP bei `/connect/authorize` angeforderte Scope-Satz wird an den vorgelagerten IdP weitergeleitet, **gefiltert auf den Standard-OIDC-Satz** (`openid`, `profile`, `email`, `address`, `phone`), wobei `openid` immer enthalten ist. Alles andere, was die RP angefordert hat (eigene API-Scopes, `offline_access`, …), wird vor dem vorgelagerten Aufruf verworfen: Ein strikter IdP wie Google gibt bei unbekannten Werten `invalid_scope` zurück, und der vorgelagerte Dienst muss den Benutzer nur identifizieren; die eigenen Scopes der RP werden bei Authagonal-ausgestellten Token honoriert, nicht bei vorgelagerten. Welche Claims der vorgelagerte IdP scope-gesteuert auf das id_token legt, kommen zu Authagonal zurück, werden auf dem Cookie-Ticket als `federated:<name>`-Claims abgelegt und werden bei der nächsten `/connect/authorize`-Durchquerung in `OidcSubject.FederationClaims` übernommen. Von dort gibt `ProtocolTokenService` sie erneut auf Authagonal-ausgestellten Token aus, abgesichert durch dieselbe `Scope.UserClaims`-Positivliste, die auch `CustomAttributes` absichert. Bei Schlüsselkollisionen gewinnen die Föderationswerte.

Nettoeffekt: Es gibt keine Positivliste von Claims pro Verbindung, die gepflegt werden müsste. Jeder Nicht-Protokoll-Claim, den der vorgelagerte Dienst auf das id_token legt, wird erfasst; welche davon die nachgelagerten Token erreichen, steuert die `UserClaims`-Einstellung des nachgelagerten Scopes: Deklarieren Sie den Claim dort, und der Wert wird durchgereicht.

`FederationClaims` übersteht Refresh-Rotationen unabhängig von `CustomAttributes`, sodass sitzungsbezogener Föderationskontext (zum Beispiel ein beim ursprünglichen Autorisierungsaufruf erfasstes Share-Link-Token) erhalten bleibt, während benutzerbezogene Attribute weiterhin bei jedem Zugriff frisch aus dem Benutzer-Store gelesen werden.

## Passthrough-Abfrageparameter

`OidcProviderConfig.PassthroughParams` ist eine Positivliste von Abfrageschlüsseln pro Verbindung, die von der ursprünglichen `/authorize`-Anfrage auf die Autorisierungs-URL des vorgelagerten IdP durchgereicht werden. Der Standardsatz (`scope`, `state`, `nonce`, PKCE) wird immer weitergeleitet; dies gilt für zusätzliche, von der RP festgelegte Werte, etwa eine einmalige Anmeldeinformation, die der vorgelagerte Dienst zur Authentifizierung benötigt (zum Beispiel `link_token` bei Share-Link-IdPs).

Ist ein Schlüssel auf der Positivliste, entnimmt Authagonal seinen Wert der ursprünglichen `/authorize`-Abfrage (mitgeführt über `returnUrl`) und hängt ihn an die vorgelagerte URL an. Alles, was nicht auf der Positivliste steht, wird stillschweigend verworfen.

## Sitzungsdauer-Obergrenze

`OidcProviderConfig.SessionExpClaim` ist der optionale Name eines id_token-Claims (Unix-Sekunden), dessen Wert die lokale Sitzungsdauer begrenzt. Ist er vorhanden, wird der vorgelagerte Wert als `session_max_exp` auf dem Cookie-Ticket und in den ausgestellten Auth-Code übernommen; Access-, id- und Refresh-Token werden so begrenzt, dass kein Token (auch nicht aus Rotationen neu ausgestellte) die vorgelagerte Sitzung überdauert. Nützlich, wenn der vorgelagerte IdP kürzere Sitzungsgrenzen erzwingt, als Authagonal standardmäßig vorsehen würde.

## Sicherheitsfunktionen

- **PKCE**: code_challenge mit S256 bei jeder Autorisierungsanfrage
- **Nonce-Validierung**: Nonce wird zusammen mit dem State gespeichert, muss im id_token vorhanden sein und übereinstimmen
- **State-Validierung**: einmal verwendbar (wird atomar über `IOidcStateStore` konsumiert, mit Ablaufzeit gespeichert) **und browsergebunden**: Ein auf `/oidc` begrenztes `SameSite=Lax`-Cookie wird bei der Anmeldung gesetzt und muss beim Callback mit dem `state` übereinstimmen, sodass ein Angreifer keinen selbst gestarteten Föderationsablauf abschließen und die Callback-URL an ein Opfer weitergeben kann (Login-CSRF)
- **id_token-Signaturvalidierung**: Schlüssel werden vom JWKS-Endpunkt des IdP abgerufen; Aussteller, Zielgruppe und Gültigkeitsdauer werden validiert
- **Userinfo-Fallback**: Enthält das id_token keine E-Mail-Adresse, wird der Userinfo-Endpunkt versucht. Das `sub` des Userinfo-Endpunkts muss mit dem `sub` des id_tokens übereinstimmen (OIDC Core 5.3.2), andernfalls wird die Antwort ignoriert
- **Stabile Identitätsverknüpfung**: Ein wiederkehrender Benutzer wird über Anbieter + `sub` aufgelöst, niemals allein über die E-Mail-Adresse. Das Verknüpfen einer föderierten Identität mit einem **bereits bestehenden** lokalen Konto per E-Mail erfordert, dass die `AllowedDomains` der Verbindung die Domäne dieser E-Mail-Adresse abdecken (die ausdrückliche Bürgschaft des Administrators, dass der IdP sie besitzt). Ein vom vorgelagerten Dienst behauptetes `email_verified` reicht *nicht* aus, um ein bestehendes Konto zu übernehmen
- **Domänen-Durchsetzung**: Ist `AllowedDomains` gesetzt, darf die Verbindung nur Identitäten innerhalb dieser Domänen behaupten (andernfalls `access_denied`)
- **JIT-Opt-out**: `DisableJitProvisioning` weist unbekannte Benutzer ab, anstatt sie automatisch anzulegen
- **Open-Redirect-Schutz**: `returnUrl` muss ein relativer, seiteninterner Pfad sein; protokollrelative Formen (`//`) und Formen mit Backslash werden abgelehnt
- **Lokale MFA gilt weiterhin**: Föderation belegt nur den ersten Faktor. Ein Benutzer, der für MFA registriert ist (oder dessen Client-Richtlinie MFA verlangt), wird nach dem Callback über die lokalen MFA-Abfrage-/Einrichtungsseiten geleitet, anstatt direkt angemeldet zu werden; erst dann trägt die Sitzung die MFA-Kennzeichnung

## Azure AD-Besonderheiten

Azure AD gibt E-Mail-Adressen manchmal als JSON-Array im `emails`-Claim zurück (insbesondere bei B2C). Authagonal berücksichtigt dies, indem sowohl der `email`-Claim als auch das `emails`-Array geprüft werden.

## Unterstützte Anbieter

Jeder OIDC-konforme Anbieter, der Folgendes unterstützt:
- Authorization-Code-Ablauf
- PKCE (S256)
- Discovery-Dokument (`.well-known/openid-configuration`)

Getestet mit:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
</content>
</invoke>
