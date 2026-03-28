---
layout: default
title: Schnellstart
locale: de
---

# Schnellstart

Bringen Sie Authagonal in 5 Minuten lokal zum Laufen.

## 1. Server starten

```bash
docker compose up
```

Dies startet Authagonal unter `http://localhost:8080` mit Azurite als Speicher.

## 2. Funktionsfaehigkeit ueberpruefen

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Client registrieren

Fuegen Sie einen Client zu Ihrer `appsettings.json` hinzu (oder uebergeben Sie ihn ueber Umgebungsvariablen):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Clients werden beim Start initialisiert -- sicher bei jeder Bereitstellung ausfuehrbar.

## 4. Login initiieren

Leiten Sie Ihre Benutzer weiter an:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

Der Benutzer sieht die Login-Seite, authentifiziert sich und wird mit einem Autorisierungscode zurueckgeleitet.

## 5. Code eintauschen

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Antwort:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Funktionsfaehige Demo

Das Verzeichnis `demos/sample-app/` enthaelt eine vollstaendige React SPA + API, die den gesamten oben beschriebenen OIDC-Ablauf implementiert. Anweisungen finden Sie in der [demos README](https://github.com/DrawboardLtd/authagonal/tree/master/demos).

## Naechste Schritte

- [Konfiguration](configuration) -- vollstaendige Referenz aller Einstellungen
- [Erweiterbarkeit](extensibility) -- als Bibliothek hosten, benutzerdefinierte Hooks hinzufuegen
- [Branding](branding) -- Login-Oberflaeche anpassen
- [SAML](saml) -- SAML-SSO-Anbieter hinzufuegen
- [Bereitstellung](provisioning) -- Benutzer in nachgelagerte Anwendungen bereitstellen
