---
layout: default
title: Startseite
locale: de
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 Authentifizierungsserver für .NET, mit austauschbarem Cloud-Speicher als Backend -- Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager).

Eine einzelne, eigenständige Bereitstellung. Server und Login-Oberfläche werden als ein Docker-Image ausgeliefert -- die SPA wird vom selben Ursprung wie die API bereitgestellt, sodass Cookie-Authentifizierung, Weiterleitungen und CSP ohne Cross-Origin-Komplexität funktionieren.

> **Lieber ein verwalteter Dienst?** [Authagonal Cloud](https://authagonal.io) betreibt das alles für Sie -- mandantenfähig, jede Funktion in jedem Tarif, keine SSO-Gebühren pro Verbindung. → [authagonal.io](https://authagonal.io)

## Hauptfunktionen

- **OIDC-Anbieter** -- authorization_code + PKCE, client_credentials, refresh_token-Gewährungstypen mit einmaliger Rotation
- **SAML 2.0 SP** -- Eigenentwicklung mit vollständiger Azure AD-Unterstützung (signierte Antwort, Assertion oder beides)
- **Dynamische OIDC-Föderation** -- Verbindung mit Google, Apple, Azure AD oder einem beliebigen OIDC-konformen IdP
- **TCC-Bereitstellung** -- Try-Confirm-Cancel-Bereitstellung in nachgelagerte Anwendungen zum Zeitpunkt der Autorisierung
- **Anpassbare Login-Oberfläche** -- Laufzeitkonfiguration über eine JSON-Datei -- Logo, Farben, benutzerdefiniertes CSS -- kein Neuaufbau erforderlich
- **Auth-Hooks** -- `IAuthHook`-Erweiterbarkeit für Audit-Protokollierung, benutzerdefinierte Validierung, Webhooks
- **Kompositionsfähige Bibliothek** -- `AddAuthagonal()` / `UseAuthagonal()` zum Hosten in Ihrem eigenen Projekt mit benutzerdefinierten Service-Überschreibungen
- **Austauschbarer Cloud-Speicher** -- Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager); kostengünstige, serverlose Backends
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
