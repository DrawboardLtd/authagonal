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

- **OIDC-Anbieter** -- authorization_code + PKCE, client_credentials, refresh_token, device_code-Gewährungstypen mit einmaliger Rotation
- **SAML 2.0 SP** -- Eigenentwicklung mit vollständiger Azure AD-Unterstützung (signierte Antwort, Assertion oder beides), ein SP-Schlüsselpaar pro Verbindung für signierte AuthnRequests + `EncryptedAssertion`-Entschlüsselung und Single Logout (SP- und IdP-initiiert)
- **Dynamische OIDC-Föderation** -- Verbindung mit Google, Apple, Azure AD oder einem beliebigen OIDC-konformen IdP
- **Multi-Faktor-Authentifizierung** -- TOTP, WebAuthn/Passkeys, Wiederherstellungscodes; Richtlinie pro Client (`Disabled` / `Enabled` / `Required`) mit `IAuthHook`-Übersteuerung pro Benutzer, auch für föderierte Anmeldungen erzwungen
- **SCIM 2.0-Bereitstellung** -- eingehende Benutzer-/Gruppenbereitstellung von Entra ID, Okta, OneLogin; cursorbasierte Auflistung und blind-index-gestützte `eq`-Filter
- **OAuth-Zustimmungsbildschirm** -- Zustimmung pro Client mit scope-bewusster erneuter Abfrage und Verwaltung der Gewährungen
- **Device Authorization Grant** -- RFC 8628-Ablauf für eingabebeschränkte Geräte (Smart-TVs, CLIs, IoT)
- **Token-Introspektion** -- RFC 7662, damit Ressourcenserver die Token-Gültigkeit überprüfen können
- **Back-Channel-Logout** -- OIDC Back-Channel Logout 1.0-Benachrichtigungen an vertrauende Parteien
- **DSGVO-Selbstbedienung** -- Datenexport und geplante Kontolöschung über die gehostete Kontoseite
- **TCC-Bereitstellung** -- Try-Confirm-Cancel-Bereitstellung in nachgelagerte Anwendungen zum Zeitpunkt der Autorisierung
- **Anpassbare Login-Oberfläche** -- Laufzeitkonfiguration über eine JSON-Datei -- Logo, Farben, benutzerdefinierte CSS-Eigenschaften -- kein Neuaufbau erforderlich; lokalisiert in 10 Sprachen
- **Auth-Hooks** -- `IAuthHook`-Erweiterbarkeit für Audit-Protokollierung, benutzerdefinierte Validierung, Webhooks
- **PII-Verschlüsselungs-Schnittstellen** -- `IFieldCipher` / `IIndexTokenizer`-Erweiterungspunkte für Verschlüsselung auf Feldebene im Ruhezustand mit schlüsselbasierter Blind-Index-Suche (HMAC); Wiederherstellungscodes über `ISecretProvider` verschlüsselt
- **HashiCorp Vault Transit** -- Remote-JWT-Signierung ohne lokalen Zugriff auf den privaten Schlüssel
- **Kompositionsfähige Bibliothek** -- `AddAuthagonal()` / `UseAuthagonal()` zum Hosten in Ihrem eigenen Projekt mit benutzerdefinierten Service-Überschreibungen
- **Native AOT-Bereitschaft** -- IL-Trimming und quellcodegenerierte JSON-Serialisierung für schnellen Start
- **Austauschbarer Cloud-Speicher** -- Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager); kostengünstige, serverlose Backends
- **Sicherung & Wiederherstellung** -- inkrementelle Sicherungen (änderungsprotokollgesteuert mit einem Voll-Scan-Rückfall), Integritätsprüfung, tombstone-basierte Löschverfolgung
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

Starten Sie mit der [Installationsanleitung](installation) oder springen Sie direkt zum [Schnellstart](quickstart). Um Authagonal in Ihrem eigenen Projekt zu hosten, siehe [Erweiterbarkeit](extensibility). Für die Datenverwaltung, siehe [Sicherung & Wiederherstellung](backup-restore). Für den vollständigen Änderungsverlauf, siehe das [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
