---
layout: default
title: Home
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 authentication server backed by Azure Table Storage.

A single, self-contained deployment. The server and login UI ship as one Docker image — the SPA is served from the same origin as the API, so cookie auth, redirects, and CSP all work without cross-origin complexity.

## Key Features

- **OIDC Provider** — authorization_code + PKCE, client_credentials, refresh_token, device_code grants with one-time rotation
- **SAML 2.0 SP** — homebrew implementation with full Azure AD support (signed response, assertion, or both)
- **Dynamic OIDC Federation** — connect to Google, Apple, Azure AD, or any OIDC-compliant IdP
- **OAuth Consent Screen** — per-client consent with scope-aware re-prompt and grant management
- **Device Authorization Grant** — RFC 8628 flow for input-constrained devices (smart TVs, CLIs, IoT)
- **Token Introspection** — RFC 7662 for resource servers to verify token validity
- **Back-Channel Logout** — OIDC Back-Channel Logout 1.0 notifications to relying parties
- **TCC Provisioning** — Try-Confirm-Cancel provisioning into downstream apps at authorize time
- **Brandable Login UI** — runtime-configurable via a JSON file — logo, colors, CSS custom properties — no rebuild needed
- **Auth Hooks** — `IAuthHook` extensibility for audit logging, custom validation, webhooks
- **HashiCorp Vault Transit** — remote JWT signing without local private key access
- **Composable Library** — `AddAuthagonal()` / `UseAuthagonal()` to host in your own project with custom service overrides
- **Native AOT Ready** — IL trimming and source-generated JSON serialization for fast startup
- **Azure Table Storage** — low-cost, serverless-friendly storage backend
- **Admin APIs** — user CRUD, SAML/OIDC provider management, SSO domain routing, token impersonation

## Architecture

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

Get started with the [Installation](installation) guide or jump straight to the [Quick Start](quickstart). To host Authagonal in your own project, see [Extensibility](extensibility). For data management, see [Backup & Restore](backup-restore). For the full change history, see the [Changelog](https://github.com/DrawboardLtd/authagonal/blob/master/CHANGELOG.md).
