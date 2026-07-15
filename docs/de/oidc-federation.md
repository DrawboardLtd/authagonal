---
layout: default
title: OIDC-Foederation
locale: de
---

# OIDC-Foederation

Authagonal kann die Authentifizierung an externe OIDC-Identitaetsanbieter foederieren (Google, Apple, Azure AD usw.). Dies ermöglicht Ablaeufe im Stil von "Mit Google anmelden", während Authagonal der zentrale Authentifizierungsserver bleibt.

## Funktionsweise

1. Benutzer gibt seine E-Mail auf der Login-Seite ein
2. SPA ruft `/api/auth/sso-check` auf -- wenn die E-Mail-Domain mit einem OIDC-Anbieter verknuepft ist, ist SSO erforderlich
3. Benutzer klickt auf "Weiter mit SSO" -> wird zum externen IdP weitergeleitet
4. Nach der Authentifizierung leitet der IdP zurück zu `/oidc/callback`
5. Authagonal validiert das id_token, erstellt/verknuepft den Benutzer und setzt ein Sitzungs-Cookie

## Einrichtung

### 1. OIDC-Anbieter erstellen

**Option A -- Konfiguration (empfohlen für statische Setups):**

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

Anbieter werden beim Start initialisiert. Das `ClientSecret` wird über `ISecretProvider` geschuetzt (Key Vault wenn konfiguriert, sonst Klartext). SSO-Domainzuordnungen werden automatisch aus `AllowedDomains` registriert.

**Option B -- Admin-API (für Laufzeitverwaltung):**

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

### 2. SSO-Domainrouting

Wenn `AllowedDomains` angegeben ist (in der Konfiguration oder über die Create-API), werden SSO-Domainzuordnungen automatisch registriert. Ohne Domainrouting können Benutzer weiterhin über `/oidc/{connectionId}/login` zum OIDC-Login geleitet werden.

## Endpunkte

| Endpunkt | Beschreibung |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initiiert OIDC-Login. Generiert PKCE + state + nonce, leitet zum Autorisierungsendpunkt des IdP weiter. |
| `GET /oidc/callback` | Verarbeitet den IdP-Callback. Tauscht den Code gegen Token, validiert das id_token, erstellt/meldet den Benutzer an. |

## Sicherheitsfunktionen

- **PKCE** -- code_challenge mit S256 bei jeder Autorisierungsanfrage
- **Nonce-Validierung** -- Nonce im State gespeichert, im id_token verifiziert
- **State-Validierung** -- einmalig verwendbar, in Azure Table Storage mit Ablaufzeit gespeichert
- **id_token-Signaturvalidierung** -- Schlüssel vom JWKS-Endpunkt des IdP abgerufen
- **Userinfo-Fallback** -- wenn das id_token keine E-Mail enthält, wird der Userinfo-Endpunkt versucht

## Azure AD-Besonderheiten

Azure AD gibt E-Mails manchmal als JSON-Array im `emails`-Claim zurück (insbesondere bei B2C). Authagonal behandelt dies, indem sowohl der `email`-Claim als auch das `emails`-Array geprueft werden.

## Unterstützte Anbieter

Jeder OIDC-konforme Anbieter, der Folgendes unterstützt:
- Authorization Code-Ablauf
- PKCE (S256)
- Discovery-Dokument (`.well-known/openid-configuration`)

Getestet mit:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
