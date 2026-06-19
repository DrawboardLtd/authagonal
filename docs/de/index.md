---
layout: default
title: Startseite
locale: de
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 Authentifizierungsserver fuer .NET, mit austauschbarem Cloud-Speicher als Backend -- Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager).

Eine einzelne, eigenstaendige Bereitstellung. Server und Login-Oberflaeche werden als ein Docker-Image ausgeliefert -- die SPA wird vom selben Ursprung wie die API bereitgestellt, sodass Cookie-Authentifizierung, Weiterleitungen und CSP ohne Cross-Origin-Komplexitaet funktionieren.

> **Lieber ein verwalteter Dienst?** [Authagonal Cloud](https://authagonal.io) betreibt das alles fuer Sie -- mandantenfaehig, jede Funktion in jedem Tarif, keine SSO-Gebuehren pro Verbindung. → [authagonal.io](https://authagonal.io)

## Hauptfunktionen

- **OIDC-Anbieter** -- authorization_code + PKCE, client_credentials, refresh_token-Gewaehrungstypen mit einmaliger Rotation
- **SAML 2.0 SP** -- Eigenentwicklung mit vollstaendiger Azure AD-Unterstuetzung (signierte Antwort, Assertion oder beides)
- **Dynamische OIDC-Foederation** -- Verbindung mit Google, Apple, Azure AD oder einem beliebigen OIDC-konformen IdP
- **TCC-Bereitstellung** -- Try-Confirm-Cancel-Bereitstellung in nachgelagerte Anwendungen zum Zeitpunkt der Autorisierung
- **Anpassbare Login-Oberflaeche** -- Laufzeitkonfiguration ueber eine JSON-Datei -- Logo, Farben, benutzerdefiniertes CSS -- kein Neuaufbau erforderlich
- **Auth-Hooks** -- `IAuthHook`-Erweiterbarkeit fuer Audit-Protokollierung, benutzerdefinierte Validierung, Webhooks
- **Kompositionsfaehige Bibliothek** -- `AddAuthagonal()` / `UseAuthagonal()` zum Hosten in Ihrem eigenen Projekt mit benutzerdefinierten Service-Ueberschreibungen
- **Austauschbarer Cloud-Speicher** -- Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager); kostenguenstige, serverlose Backends
- **Admin-APIs** -- Benutzer-CRUD, SAML/OIDC-Anbieterverwaltung, SSO-Domainrouting, Token-Impersonation

## Architektur

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Starten Sie mit der [Installationsanleitung](installation) oder springen Sie direkt zum [Schnellstart](quickstart). Um Authagonal in Ihrem eigenen Projekt zu hosten, siehe [Erweiterbarkeit](extensibility).
